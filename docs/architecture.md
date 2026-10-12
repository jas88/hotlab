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
   The MVP instead accepts plain RDP on 3389 and routes on the first PDU (see
   *Gateway implementation tiers*); the RD Gateway endpoint remains the target for
   HTTPS-only networks.

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
  RDS Connection Brokers use to route sessions in farms. mstsc reconnects
  automatically to the in-guest session. Brief blip, no timeout, no manual retry.
  *Who* emits the redirect depends on the gateway tier: a pass-through relay
  (T0–T2) cannot inject PDUs into the encrypted stream, so there qemu-rdp must emit
  it; only a session-owning gateway (T3) can. Options in order of cost are under
  *Handover options by tier* below. IronRDP models redirection client-side;
  emitting it server-side is custom but standard-protocol work.

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
sessions. Dependency: qemu-rdp needs QEMU started with `-display dbus`; with the RFC
experimental, expect custom VM `args` or a patched pve-qemu initially (to test against
the spike host).

#### Gateway implementation tiers (as built / planned)

**As built — T0, `src/Hotlab.Gateway`: 3389 byte relay.**

- Reads the plaintext first PDU (TPKT + X.224 Connection Request), routes on the
  `Cookie: mstshash=` value, starts the VM via the Proxmox API, waits for
  guest-agent IP and an open RDP port, forwards the first PDU verbatim, then splices.
- TLS/CredSSP stay **end to end** client↔guest. The gateway holds no certificates
  and never sees credentials (this corrects the plaintext-credentials assumption in
  *Verifying client credentials*, which applies only from T2 up).
- Caveats:
  - The cookie is **unauthenticated**: anyone reaching 3389 can trigger a VM start
    by claiming a name. The guest login still gates access; start-rate limiting
    belongs in the broker.
  - mstsc truncates mstshash to **9 chars including any `DOMAIN\` prefix** (per F5
    DevCentral and Zeek community threads; MS-RDPBCGR 2.2.1.1 sets no limit). Routing
    is exact match, then unique-prefix match, else reject.
  - No cookie (nameless connection) → reject. Doing better needs T3.
  - Holding the TCP connection while the VM boots spends mstsc's ~30 s
    connect timeout (see the timeout wrinkle above); the console-first handover
    below is the fix.

**In progress — T1: signed routing token in `loadbalanceinfo:s:`.**

- mstsc sends the `.rdp` `loadbalanceinfo` value verbatim + CRLF as the X.224
  `routingToken` — the RD Connection Broker mechanism. Format
  `hotlab:v1:<user>:<expiry>:<HMAC-SHA256 sig>`; key held as a systemd credential.
- Gives an authenticated identity without the cookie, for **click-time portal
  `.rdp` files only**. Webfeed-delivered files are cached (see *Choosing among N
  targets*), so they carry only the non-secret lab name and fall back to name
  routing.
- The token is a **bearer credential for routing only** — short expiry, scoped to
  (user, lab); guest login still applies.

**T2 — TLS-terminating relay (planned, not committed to).**

- NLA cannot be passed through a re-encrypting proxy: CredSSP binds to the TLS
  public key the client sees (`pubKeyAuth`), so the gateway must *be* the CredSSP
  server. That means verifying NTLM (needs the user's NT hash) or Kerberos (AD keytab
  for the gateway SPN). LDAP bind-as-user cannot satisfy NTLM. The cost is identity
  infrastructure more than code.
- Mitigation: xrdp does no NLA, so for Linux guests the gateway→guest leg is TLS-only
  and the gateway injects per-session credentials by rewriting the Client Info PDU
  (`TS_INFO_PACKET`, `INFO_AUTOLOGON`). Windows guests need client-side CredSSP from
  the gateway using the broker-generated per-session password.
- Gateway's certificate replaces each guest's self-signed one (a UX gain).
- When the legs negotiate different protocols, patch `serverSelectedProtocol` in GCC
  Client Core Data.

**T3 — session-owning proxy (milestone).**

- Answers MCS/GCC itself; translates channel IDs, MCS user IDs and share IDs between
  client and each backend; swaps backends invisibly via Deactivation-Reactivation
  (MS-RDPBCGR 1.3.1.3); can emit Server Redirection PDUs; can render its own
  holding/login screen — the only smart answer to a truly nameless connection.
- Don't hand-roll this in .NET (no mature RDP server library). Build on **IronRDP**
  (server, connector, PDU crates; Devolutions Gateway already does credential
  injection), as the separate, less-trusted gateway process with the .NET broker as
  policy decision point (*Component trust boundaries*).

#### Handover options by tier (qemu-rdp → in-guest)

1. **Drop and auto-reconnect** (T0/T1): broker closes the qemu-rdp leg when the guest
   is ready and routes the reconnect to xrdp/TermService. No protocol work.
   Unverified: mstsc auto-reconnect likely requires the server to have issued an
   auto-reconnect cookie (Save Session Info / ARC); whether qemu-rdp does is to test.
2. **Patch qemu-rdp to emit a Server Redirection PDU** on a broker signal. The PDU
   can carry LoadBalanceInfo, so mstsc reconnects to the gateway with a T1 session
   token. Smallest change that works through a pass-through relay — the "gateway
   rather than qemu-rdp" preference holds at T3, not T0–T2. To test: whether mstsc
   honours a redirection mid-session rather than only during logon.
3. **At T3**: the gateway emits the redirect itself, or swaps backends invisibly via
   deactivate/reactivate + channel-ID translation.

Upside of any qemu-rdp path: the per-VM socket appears ~5 s after start, so the
first-connect timeout stops mattering.

**Recommended path**: finish T0+T1; prototype handover option 2 against the real
Proxmox host; treat T3 as the IronRDP gateway milestone.

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
  pattern) for single-login launch — concretely the T1 `loadbalanceinfo` routing
  token (*Gateway implementation tiers*).

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
- Injection on the native-client path needs gateway tier T2+ (Client Info PDU
  rewrite for xrdp, gateway-side CredSSP for Windows); at T0/T1 the user types the
  guest credentials. See *Gateway implementation tiers*.
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
- On the native-client path, what the gateway sees depends on its tier. T0/T1 relay
  CredSSP end to end and see no credentials. From T2 the gateway is the CredSSP
  server and receives delegated username+password — but only *after* NTLM/Kerberos
  authentication succeeds, which needs the NT hash or an AD keytab; LDAP
  bind-as-user cannot back that step (see *Gateway implementation tiers*). Wherever
  credentials are seen: memory only, zeroized after use, never logged; forwarded to
  the broker's auth endpoint; the gateway holds **no directory credentials of its
  own** (a T2 keytab would be the exception, and a reason to defer T2).
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
- Feed-delivered `.rdp` files contain no secrets (established above), so client-side
  caching and casual sharing leak nothing. Click-time portal `.rdp` files carry a T1
  routing token: a short-lived bearer credential for routing only — sharing one lets
  someone start/reach the lab's login screen until expiry, not log in.

## Personas and management UX

One web portal, role-based views. Two authoring surfaces split by data shape:
infrastructure-shaped data is YAML-in-git (admin); people-shaped data is DB-backed
self-service UI (instructor). GitOps boundary: **admin lab definitions and policy
envelopes in git; classes, rosters, entitlements, and instructor-published lab
definitions in the DB**.

### Admin

- Lab/pod definitions and templates: YAML in git, validated by the broker on load
  (`hotlab validate` as a CI/dry-run check). Capacity caps per group/class.
- Health view: active sessions, reaper/orphan activity, capacity, backup age.

### Instructor (the daily-driver UX)

- **Class**: name, term dates, lab assignments each with an availability window
  ("opens week 4, closes week 6") and per-student limits.
- **Enrollment without roster data entry**: instructor generates an invite code/link;
  students self-register (or LDAP-authenticate later) straight into the class. CSV
  import as fallback; LDAP-group mapping eventually.
- **Live monitoring**: active sessions per class with per-student actions —
  terminate, extend time, **reset lab** (destroy + fresh clone: the "start me over"
  button).
- **Shadowing** (later milestone): instructor views a student's screen. The qemu-rdp
  console path naturally supports multiple viewers of one display — a strong reason
  to keep the console path alive after in-guest upgrade exists (in-guest shadowing is
  messy: Windows shadow sessions work, xrdp barely).
- **End of term**: archiving a class revokes entitlements, ends sessions, and
  destroys class persistent VMs after a retention window — cleanup as a first-class
  lifecycle event.

#### Instructor lab authoring: workbench → publish

Instructors create their own single-VM labs without admin involvement — the feature
Netlab makes an admin ritual. Canonical example: *"I want an Alpine Linux VM with
MariaDB plus this SQL file"* → pick Alpine base, install MariaDB in the workbench,
upload the SQL file, import it, publish, assign to class. Minutes, self-service.

1. **New lab**: pick from an admin-curated **base catalog** ("Debian 12 + xrdp
   base", "Alpine base", "Windows 11 base"). The broker **full-clones** (not linked)
   into a workbench VM, so the published result is independent and the base can be
   retired freely.
2. **Workbench session**: same RDP access path as any lab, but persistent-style
   lifecycle — idle policy stops, never destroys; lives until published or
   explicitly discarded (auto-destroy only warns, never surprises).
3. **Getting files in**: portal upload box on the workbench page — broker pushes
   files into the guest via QEMU guest agent `file-write` into a well-known folder
   (`~/lab-files/`); RDP drive redirection/clipboard also available on the in-guest
   path.
4. **Publish**: broker shuts down, runs a seal step (clear machine-id/SSH host
   keys/cloud-init state on Linux; optional sysprep on Windows), converts to
   template + snapshot, and auto-generates a single-VM lab definition assignable to
   the instructor's classes immediately. Re-publish = new template version (old one
   drains and retires per the template lifecycle rules).

Policy guardrails keeping the security story intact:

- **The envelope stays admin-controlled**: instructors customize *inside* the VM;
  network isolation, Internet opt-in, resource sizes, storage quota, and template
  count come from an admin-defined policy envelope (YAML in git). Publishing cannot
  escalate a lab's network or resource profile.
- **Workbench networking ≠ published-lab networking**: authoring typically needs
  NAT'd egress (installing packages); the published lab reverts to isolation
  defaults. The distinction is explicit to prevent labs shipping with the authoring
  network attached.
#### One lab concept, not two tiers

There is deliberately **no admin-lab vs instructor-lab type split** — a hard
two-tier model ossifies (features land tier-first, promotion becomes migration).
Instead, one `LabDefinition` with three orthogonal properties:

- **Provenance** (`origin: git | published`): git is an *import pipeline* — the
  broker syncs YAML definitions into the same catalog table the publish flow writes
  to. One downstream code path for assignment, sessions, teardown.
- **Ownership & visibility** (`owner`, `visibility: private | class | org`):
  instructor labs start private; promotion to the curated org catalog is a
  flag/owner change, not a conversion. The same mechanism gives
  instructor-to-instructor template sharing.
- **Capability gating on provenance**: dangerous capabilities (Internet opt-in,
  multi-VM topologies, custom networks, large resource grants) require the reviewed
  path (git) or an explicit per-capability admin approval flag on the definition.
  One enforcement point in the broker checks requested capabilities against
  provenance/approvals.

The "base catalog" follows the same collapse: bases are templates flagged
`usableAsBase`, not a separate concept. Multi-VM *authoring* UI remains a later
milestone, but a multi-VM definition is not a different type — just a capability
most published labs don't request yet.

### Student

"My labs" only: launch, time remaining, reset-my-lab, download `.rdp` / feed URL.

### Data model consequence

Entitlement is not a flat user↔lab map:

```
User ↔ Membership ↔ Class ↔ LabAssignment ↔ LabDefinition
                                  │
User ──────────── Session ────────┘   (one per user per assignment)
```

Students receive the union of their classes' assignments within availability windows.

v1 scope: class CRUD, invite-code enrollment, assignment windows, session list with
terminate/extend/reset. Shadowing and CSV/LDAP enrollment follow.

## Storage, persistence, backups, and operations

### State taxonomy — what persists where

| State | Owner | On loss |
|-------|-------|---------|
| Lab definitions & policy | **YAML in git**, loaded/validated by broker | restore from git |
| Entitlements, local users (argon2id hashes) | broker DB | restore DB backup |
| Session runtime state | broker DB **as index**; Proxmox is source of truth | reconcile against PVE |
| Audit log | broker DB (append-only, retention policy) | restore DB backup |
| Secrets (PVE token, token-signing keys) | systemd credentials (TPM2-sealed) | re-issue/rotate |
| Templates & persistent VMs | Proxmox storage | restore from PBS |
| Ephemeral clones | Proxmox storage | **never backed up — by definition disposable** |

### Proxmox as source of truth + reconciliation

Every resource the broker creates follows a naming/tagging convention
(`hotlab-sess-<id>` resource pools, `hotlab` VM tags, per-session VNet names). The
broker also writes an **intent record before each Proxmox API call** ("creating VM
1234 for session S"). Consequences:

- Crash mid-provision always leaves a reconcilable trail.
- The **reaper doubles as a consistency checker**: PVE objects matching the
  convention with no live session row → destroy; session rows with no PVE objects →
  mark failed. This loop is most of day-2 ops.
- Grace/idle/max-session timers persist as **absolute UTC deadlines** in the DB, not
  in-memory countdowns — restart-safe.
- Broker operations are idempotent/resumable.

### Database

**SQLite in WAL mode via EF Core** (Microsoft.Data.Sqlite provider). Right ops
profile for a single-broker appliance: no server, single-file backup (`VACUUM INTO`
on a timer, or Litestream for continuous off-host replication). EF Core keeps the
PostgreSQL escape hatch real — same entity model/LINQ, re-scaffold migrations for
Npgsql if HA/multi-broker ever materializes — and provides the
migrations-at-startup requirement via `MigrateAsync()`.

Settings from day one: `journal_mode=WAL`, `synchronous=NORMAL`, `busy_timeout`,
`foreign_keys=ON`; a single serialized write path (channel/queue) since SQLite is
single-writer; DB on **local storage only**, never NFS/CIFS. Audit rows share the
file with a retention sweep — no second database until size demands it.

### Backups

The ephemeral model inverts the problem — most of the estate is explicitly *not*
backed up:

- **Templates + persistent VMs**: Proxmox Backup Server; ephemeral clones excluded
  via pool/tag filters or `backup=0` at clone time.
- **Broker DB**: periodic `VACUUM INTO` + off-host copy, or Litestream.
- **Lab definitions**: already in git.
- **Restore flow**: restore DB + secrets → start broker → reconciler cleans up
  anything half-existing → templates from PBS. Everything else rebuilds itself.

### Guest storage realities

- Linked clones require capable storage: ZFS, LVM-thin, Ceph RBD, or qcow2-on-dir.
- A template with live linked clones cannot be deleted or storage-migrated →
  template updates are **new version + drain + retire** (`tmpl-kali-v3` convention),
  never in-place edits.
- Thin-clone growth is bounded by session lifetime; **RAM is the real capacity
  ceiling** — per-user/per-group concurrent-session caps are first-class config and
  double as the capacity-planning knob.

### Operations

- Broker on a dedicated management VM/LXC, **not reachable from the lab access
  network**; systemd service with `LoadCredentialEncrypted`.
- Structured logs to journald; `/metrics` (Prometheus) + `/health` endpoints. Key
  series: active sessions, clone latency, provisioning failures, reaper actions,
  auth failures, DB-backup age.
- **Drain mode** for upgrades: refuse new sessions, let existing ones finish, then
  update and restart (schema migrations at startup).
- Alert on: provisioning error rate, orphan count from the reaper, stale backups.

## Stretch: cloud burst (future)

Someday-feature: give the broker Azure/AWS credentials; when local capacity is
exhausted (user 11 of 10), the next session spawns in the cloud instead.

**Preferred shape: Proxmox-in-cloud as a second endpoint, not a second driver.**
Nested virtualization makes PVE runnable on ordinary cloud instances — Azure
v3-series onward for years; AWS added it to 8th-gen C8i/M8i/R8i in Feb 2026 (no
more bare-metal requirement). Nested overhead (~10% CPU steal, 15–20% disk) is fine
for labs. The broker starts a cloud instance running PVE and speaks the same
Proxmox API to it:

- One code path: same clone/pool/tag/reaper logic against a second endpoint.
- Linked clones survive: templates replicate as PVE templates (PBS restore or
  image sync — PVE-native pipeline, no AMI conversion), then clone instantly there.
- qemu-rdp console and shadowing survive — we own the host.
- Pod isolation is internal to the PVE host, so the cloud fabric's no-bridging
  rules don't matter; only gateway→entry-VM crosses it (NAT on the cloud host or a
  WireGuard tunnel).
- Economics: one large instance amortizes across many lab VMs; broker scales it to
  zero, pre-warming ahead of scheduled classes (cold start is minutes).

Hedges already in the v1 data model: resource references as opaque
**(endpoint, id)** handles, never bare VMIDs; template records carry per-endpoint
availability. Burst remains a policy decision — per-class opt-in with budget caps;
default is queue-for-local-capacity. The tag/intent/reconcile reaper extends to the
cloud node unchanged and doubles as cost-runaway protection; the instance itself is
reaped like any other resource.

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
   gateway-triggered (logging in to the gateway *is* the trigger). Gateway-triggered
   is more elegant; portal-first is easier to debug and shows session status.
2. **Value of X**: the per-group grace period default, max session duration, idle timeout.
3. **Pod networking mechanism**: Proxmox SDN VNets vs. per-session VLAN tags vs.
   dedicated bridges — depends on cluster setup and scale.
4. **RDP plumbing**: IronRDP/Devolutions Gateway (covers browser + native clients) vs.
   guacd for browser plus a separate RD Gateway component for native clients.

## Decided

- **Broker stack: .NET 8+** (C#). The T0/T1 byte relay is .NET
  (`src/Hotlab.Gateway`) since it parses only the plaintext first PDU; anything that
  owns the RDP session (T3, gateway-emitted redirection) is Rust on IronRDP. The
  broker↔gateway REST/token interface keeps that split clean.
- **Identity: phased** — local users for the prototype (a PVE host is available but
  no directory server yet); LDAP bind-as-user per the security design when one exists.
- **First milestone: spike against the real PVE host** — drive clone→pool→start→
  agent-ready→destroy via the API, measure clone-to-RDP-listener timings, and test
  qemu-rdp feasibility against PVE's QEMU build.
