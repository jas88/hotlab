# Hotlab — Requirements and Architecture

Hotlab is a replacement for the NDG Netlab+ appliance, built on Proxmox VE. It brokers
authenticated user sessions onto virtual machines — spawning ephemeral VMs on demand or
connecting users to persistent ones. Every guest speaks RDP (Windows natively, Linux
via xrdp); clients connect either **in the browser over HTTPS** or with a **native RDP
client** via a gateway endpoint.

## Why replace Netlab+

Netlab+ only supports **static** VM allocation. A pod of (say) 5 pre-created VMs can be
started, stopped and rolled back automatically, but Netlab cannot clone or destroy even a
single disposable VM on demand. Every lab seat must exist in advance, which forces the
pod/reservation model: a pod is either *in use* or *vacant*, never *created when needed*.

On Proxmox, the missing capability is a single API call: a linked clone from a template
snapshot (`POST /nodes/{node}/qemu/{vmid}/clone`) completes in seconds and consumes almost
no storage. That collapses the reservation model entirely:

> **connection attempt = session = VM lifecycle**

Capacity is bounded by host resources, not by how many pods were built in advance.

## Core user flow

1. User authenticates (to the gateway or a portal — see open questions).
2. Based on the user's group/configuration, the system either:
   - **Ephemeral**: clones one or more VMs from templates, starts them, and tears them
     down when the session ends; any number of concurrent instances, resources permitting; or
   - **Persistent**: connects the user to fixed, pre-configured VM(s) that survive
     across sessions.
3. The user reaches the VM(s) via **RDP** — Windows guests natively, Linux guests via
   **xrdp** installed in the template — using either the browser (HTTPS) or a native
   RDP client pointed at the gateway.
4. On session end, ephemeral VMs are destroyed (see lifecycle below); persistent VMs are
   left running or stopped per configuration.

## Concepts

| Term | Meaning |
|------|---------|
| **Template** | A Proxmox VM template (with snapshot) that ephemeral VMs are linked-cloned from |
| **Group** | A named configuration mapping users/roles to templates (ephemeral) or VM IDs (persistent), plus policy: VM roles and count, internal networking, teardown timers, resource limits |
| **Pod** | A multi-VM group instance with its own isolated internal network — e.g. attack box + target(s) for security exercises |
| **Session** | One authenticated user's active use of a group: the set of VMs (cloned or persistent), their network, and the gateway connection(s) |
| **Ephemeral VM** | Cloned at session start, destroyed at session end |
| **Persistent VM** | Fixed VM a user connects to repeatedly; state survives sessions |

## Architecture

Three components; only the broker is new code.

```
user ──browser (HTTPS)──▶
                          Access gateway ──RDP──▶ guest VMs
user ──native RDP client─▶
                        │    ▲
                 auth / │    │ connection
                 events ▼    │ details
                      Broker ──REST API──▶ Proxmox VE
                        │                    (clone, start,
                     config DB                stop, destroy)
```

### Broker (the new component)

- Holds group definitions and user→group mappings.
- Drives the session lifecycle: authenticate → clone+start (or locate persistent VM) →
  wait for guest reachability → register connection with the gateway → monitor →
  teardown.
- Manages VMID allocation, per-session Proxmox resource pools (one pool per session makes
  cleanup trivial: destroy everything in the pool), and IP/network assignment.
- Talks to Proxmox via its REST API using an API token.

### Access gateway

Guest-side, scope is **RDP only**: every guest speaks RDP — Windows natively, Linux
via xrdp baked into the template. One guest protocol keeps credential injection and
session handling uniform.

Client-side there are **two front doors**, both required:

1. **Browser over HTTPS** — the custom menu UI plus an in-browser RDP rendering path.
2. **Native RDP clients** (mstsc, Remmina, …) — via an **RD Gateway-protocol
   endpoint** (RDP tunneled over HTTPS, which mstsc speaks natively), so native
   clients authenticate through the broker and get routed to their session's VM.

The UI will be a **custom menu system** — full Guacamole (Java webapp, its connection
registry and UI) is explicitly *not* wanted. The hard part to source is the RDP
plumbing: RDP with NLA/CredSSP and modern codecs is complex, and no mature
pure-JavaScript client exists. Candidate approaches:

- **IronRDP / Devolutions Gateway** (Rust): covers *both* doors — a WASM browser
  client for path 1 and an RD Gateway-protocol implementation for path 2. The
  dual-front-door requirement tilts the choice this way.
- **guacd without the webapp**: Guacamole's C proxy daemon plus `guacamole-common-js`
  in the custom frontend (or the `guacamole-lite` Node wrapper). Well-trodden for
  custom-UI browser access and brings clean-close vs. tunnel-drop session events, but
  has **no native-RDP ingress** — path 2 would need a separate RD Gateway component
  anyway.

A wrinkle for connect-triggered provisioning over path 2: the Windows RDP client's
default connection timeout is ~30 s per attempt, which a fresh clone+boot may exceed.
Mitigations: hold the connection at the gateway (once the HTTPS tunnel is accepted,
the clock is mstsc's patience mid-sequence — roughly 30–60 s of slack), show an
RDP-rendered "provisioning…" holding screen, or portal-first provisioning with native
RDP for connect/reconnect once the session exists. Boot-to-listener estimates (to be
measured): lean Linux template with xrdp ~10–30 s from `qm start` (listener starts
mid-boot); Windows TermService ~20–60 s. So Linux ephemeral labs likely survive first
connect behind a stalling gateway; Windows is marginal. Accepted risk: a first-connect
timeout needing one retry is tolerable, since native-client use is a power-user case
that leans toward persistent VMs (already booted).

#### Console-first connect, then upgrade (preferred sequence)

The timeout problem largely vanishes with a **two-stage connection**: attach the
client to the console-level RDP (qemu-rdp, below) within seconds of clone start — the
user watches the VM boot — then upgrade to the in-guest RDP session once the guest is
ready (QEMU guest agent reporting boot/IP triggers the switch):

- **Browser path**: the web client/gateway owns the client-facing session and simply
  switches backend source from qemu-rdp to the in-guest server. Seamless.
- **Native clients**: the standard **RDP Server Redirection PDU** — the same mechanism
  RDS Connection Brokers use to route sessions in farms. The gateway (cleaner than
  patching qemu-rdp) sends the redirect; mstsc reconnects automatically to the
  in-guest session. Brief blip, no timeout, no manual retry. IronRDP models
  redirection client-side; emitting it server-side is custom but standard-protocol
  work. (A fully terminating/re-encoding proxy would make the swap invisible, but
  costs a complete RDP server implementation — redirection is the sane choice.)

The upgrade is polish, not necessity: console RDP alone is a usable lab experience
(guest login screen, physical-console equivalent). In-guest RDP adds clipboard,
drive/audio redirection, resolution handling, and NLA credential injection so the
user lands straight in a session. Some lab types may never upgrade.

#### qemu-rdp: console-level RDP from the host

A third RDP source besides in-guest TermService/xrdp: **qemu-rdp** (Rust, built on
IronRDP) serves QEMU's D-Bus display (`-display dbus`) from the *host* — console-level
access, available within seconds of VM start, showing firmware/boot. On this path the
cold-boot timeout problem disappears: the user watches the clone boot as if at the
console. Highly relevant: an **RFC series on pve-devel (Aug 2026, A. Derumier) adds
native RDP consoles to Proxmox** — `pve-qemu-rdp` (per-VM RDP server), `pve-rdpproxy`
(auth/TLS), `pve-rdp-web` (browser client), integrated with pveproxy auth; the per-VM
socket appears within ~5 s of the API call. Status: explicitly experimental,
browser-only (no native-client ingress). Worth tracking — Proxmox converging on this
stack both validates the IronRDP direction and may eventually supply the browser
console path for free, leaving Hotlab's gateway to cover native clients and in-guest
sessions.

#### Choosing among N targets (native-client workflow)

A user may be entitled to several labs/targets. RD Gateway itself has no picker — the
client arrives knowing its target, and the gateway authorizes the (user, target) pair.
The menu layer is Microsoft's **RemoteApp and Desktop Connections webfeed** (MS-TSWP,
the RD Web Access mechanism), which Hotlab's broker can serve directly:

- The user subscribes once to the feed URL (Windows: built-in "Workspaces"); their
  entitled labs then appear as published desktops, Start-menu-integrated.
- **Caching caveat**: the client downloads the feed *and* its `.rdp` files at
  subscription/refresh time and launches from the local cache — there is no fresh
  fetch when the user clicks. The broker may render `.rdp` content dynamically per
  user when the feed is requested, but feed-delivered files must therefore contain
  only **stable, non-secret** content: gateway address + logical lab name. No
  embedded tokens — authorization happens at connect time (user authenticates to the
  gateway; gateway asks the broker to authorize and resolve "user X, lab Y" → clone
  now, route when the guest is up). One entry per *lab*, not per VM; the VM need not
  exist until launch.
- Fallback requiring no feed protocol: the web portal lists entitlements and serves a
  generated `.rdp` per target. Because these *are* produced at click time, this path
  can embed short-lived signed tokens (Devolutions Gateway's session-authorization
  pattern) for single-login launch.

### Proxmox VE

Used unmodified. Relevant facilities:

- **Linked clones** from template snapshots — near-instant, copy-on-write.
- **Resource pools** — group a session's VMs for tracking and bulk cleanup.
- **SDN / VNets** — per-session isolated internal networks for multi-VM groups
  (the one genuinely good feature of Netlab pods, reproduced on demand).
- **API tokens** with scoped privileges for the broker.
- **QEMU guest agent** — detect boot completion / obtain guest IP before handing the
  connection to the gateway.

## Session lifecycle and teardown policy

States for an ephemeral session:

```
REQUESTED → PROVISIONING → READY → ACTIVE → ┬─(clean disconnect)──────────▶ TEARDOWN
                                            └─(connection lost)─▶ GRACE ─┬─▶ TEARDOWN
                                                        (reconnect) ◀────┘
                                                            │
                                                            ▼
                                                          ACTIVE
```

- **Clean disconnect** (user logs out / closes the session deliberately): tear down
  immediately.
- **Connection loss** (network drop, browser crash, laptop lid): enter a **grace period
  of X minutes** (configurable per group). If the user reconnects within the window,
  they resume the *same* VMs; otherwise teardown proceeds.
- **Detection**: Guacamole distinguishes a user-initiated disconnect from a dropped
  tunnel, and at the RDP layer a clean logoff differs from a transport drop. The broker
  treats "clean" signals as immediate-teardown and anything else as grace-period.
- Backstop timers regardless of signal quality: a **maximum session duration** and an
  **idle timeout** (connected but no input), both per-group, so lost events can never
  leak VMs. A periodic reaper reconciles broker state against Proxmox (any VM in a
  session pool with no live session record gets destroyed).

Teardown = stop (or hard-stop after timeout) → destroy clones → remove session pool and
VNet → deregister gateway connections.

Persistent VMs skip clone/destroy; per-group policy decides whether disconnect stops the
VM, rolls it back to a snapshot, or leaves it running.

## Pods: multi-VM groups with isolated networking

Two canonical configurations, with **isolation as the default** in both:

- **Trivial case**: a single VM with no networking and no Internet access. Since access
  is RDP-only, the VM still carries one NIC on the access network so the gateway can
  reach it — "no networking" means that NIC is firewalled to gateway-only traffic
  (Proxmox firewall rules on the clone): no Internet, no peer VMs, nothing else.
- **Complex case**: a **pod** of 2+ VMs with networking between them, usually isolated
  from the outside world — the canonical example being a security exercise with an
  **attack box** and one or more **target** VMs. Internet/outside access is the
  exception, enabled per group only when an exercise requires it.

- Each session instantiating a pod gets its **own isolated internal network**
  (Proxmox SDN VNet, or a per-session VLAN tag / dedicated bridge). Because networks
  are isolated per session, cloned VMs can keep identical internal IPs across any
  number of concurrent sessions — the guarantee Netlab needed static pods for, here
  reproduced on demand.
- **Entry VMs** (e.g. the attack box) are dual-homed: one NIC on the access network so
  the gateway can reach them over RDP, one NIC on the pod's internal network.
- **Internal-only VMs** (targets) attach solely to the internal network — unreachable
  from anywhere except inside the pod, which is exactly the containment a security
  exercise needs. If direct RDP to a target is ever wanted, it becomes an entry VM too.
- Group config therefore specifies per role: template, count, entry vs. internal,
  which internal network(s) each NIC joins (a pod may define more than one segment,
  e.g. DMZ + LAN topologies), and whether outside/Internet access is permitted
  (default: no).
- Teardown destroys the VNet/bridge along with the clones (already part of the
  session-pool cleanup).

## Credential handling

The awkward corner is RDP login to a VM that didn't exist a minute ago:

- Simplest (lab-grade): fixed credentials baked into each template; the broker injects
  them into the Guacamole connection so the user never types them.
- Better: per-session random password set via cloud-init (Linux) or sysprep
  unattend/guest-agent exec (Windows) at clone time.
- Windows clones also raise hostname-uniqueness/AD-join questions — ignorable for
  standalone lab VMs, real work if labs need domain membership.

## Security design

Organizing principle: **privilege separation between gateway and broker**, and scoping
so a full compromise of any one component has a bounded blast radius. Guests —
especially attack boxes in security labs — are treated as **adversarial by default**.

### Verifying client credentials

- No stored user passwords: verification is an **LDAP bind-as-user** over LDAPS (CA
  properly verified). A minimal read-only service account only if search-then-bind is
  needed for DN resolution.
- The gateway unavoidably sees plaintext credentials on the native-client path —
  NLA/CredSSP delegates username+password; that is inherent to RDP. Therefore:
  credentials held in memory only, zeroized after use, never logged; forwarded to the
  broker's auth endpoint; the gateway holds **no directory credentials of its own**.
- Browser path can do better: OIDC against an IdP where available, so the web flow
  never hands Hotlab a reusable password.
- The broker is the single policy decision point: rate limiting, lockout, and audit
  logging of every auth attempt (success and failure) live there.
- Guest-session credentials are per-session random passwords generated by the broker,
  injected via cloud-init/guest agent at clone time and into the RDP connection —
  short-lived by construction, dead when the clone is destroyed.

### Holding Proxmox API credentials

- **API token only** — never a password, never `root@pam`. Dedicated PVE user (e.g.
  `hotlab@pve`) with a privilege-separated token bound to a custom role holding only
  what the broker does: `VM.Clone`, `VM.PowerMgmt`, `VM.Config.*`, `VM.Console`,
  `VM.Audit`, `Pool.Allocate`, plus SDN rights as needed.
- **ACL scoped to a dedicated resource-pool path and reserved VMID range, not `/`** —
  the blast-radius win: a fully compromised broker can churn Hotlab's own pool but
  cannot touch other workloads on the cluster.
- Token storage, in order of preference: **systemd credentials**
  (`LoadCredentialEncrypted`, TPM2-sealed where hardware allows) supplied to the
  broker service; else a root-owned 0600 file read at startup by a dedicated
  unprivileged service user. Not environment variables (visible via `/proc`, crash
  dumps). Vault/OpenBao only if such infrastructure already exists.
- Periodic token rotation; broker reloads credentials without restart.

### Component trust boundaries

- **Only the broker talks to Proxmox.** The gateway consumes a narrow internal API
  ("authenticate this user", "authorize user X for lab Y", "route this session")
  over mTLS or a UNIX socket with peer-credential checks, carrying short-lived signed
  session grants. Gateway compromise yields zero Proxmox access.
- **Guests have no route to the Proxmox API, the broker, or the directory.** The
  access network is one-way: gateway→guest RDP only, enforced by Proxmox firewall
  rules on every clone (complementing the per-group isolation defaults above).
- Feed/`.rdp` files contain no secrets (established above), so client-side caching
  and casual file sharing leak nothing.

## Prior art

| Project | Relevance |
|---------|-----------|
| **Apache Guacamole** | Only guacd + client JS as candidate RDP plumbing — the webapp/UI is not wanted |
| **IronRDP** (Devolutions) | Rust RDP stack with WASM browser client; Guacamole-free alternative |
| **qemu-rdp / Proxmox RDP console RFC** | Host-side console RDP via QEMU D-Bus display (IronRDP-based); pve-devel RFC Aug 2026 adds it to Proxmox (experimental, browser-only) |
| **Kasm Workspaces** | Proves the ephemeral-session UX; container-first, not Proxmox-native |
| **Ludus** | Proxmox-based security-training range automation; template/range building ideas |
| **NDG Netlab+** | The incumbent; its deployment and behaviour are documented in this repo as learned |

None combine on-demand Proxmox cloning with connection-triggered provisioning, which is
the gap Hotlab fills.

## Open questions

1. **Where auth lives**: portal-first (log in to a web UI, pick a lab, then connect) vs
   gateway-triggered (logging in to Guacamole/RDP *is* the trigger). Gateway-triggered
   is more elegant; portal-first is easier to debug and shows session status.
2. **Identity backend**: local users, LDAP/AD, or OIDC.
3. **Value of X**: the per-group grace period default, max session duration, idle timeout.
4. **Pod networking mechanism**: Proxmox SDN VNets vs. per-session VLAN tags vs.
   dedicated bridges — depends on cluster setup and scale.
5. **RDP plumbing**: IronRDP/Devolutions Gateway (covers browser + native clients) vs.
   guacd for browser plus a separate RD Gateway component for native clients.
6. **Broker implementation language/stack** — to be decided before prototyping.
