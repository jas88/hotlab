# Hotlab

On-demand Proxmox VE lab VMs behind an RDP broker (`src/Hotlab.Gateway`). See
`docs/architecture.md` for the design.

## Deploying a lab (MVP)

`Hotlab.Gateway deploy` creates one clone of a source VM per user. Each clone gets a guest
account, a random password and its own `.rdp` file. `Hotlab.Gateway teardown` removes all of
it again. Both verbs use the gateway's normal configuration (`appsettings.json` plus
environment variables such as `Hotlab__Proxmox__TokenSecret` and `Hotlab__Tokens__SigningKey`).

### Source VM prerequisites

- **Linux guest** with an RDP server (xrdp) that accepts local accounts. Creating accounts
  works on Linux only. For Windows sources, pass `--guest-user <existing account>`: the
  password is set through the agent, which works on Windows too.
- **qemu-guest-agent** installed and enabled in the guest, and `agent: 1` set in the VM's
  Options. Deploy uses the agent to create accounts (`useradd -m -s /bin/bash <user>`) and to
  set passwords.
- **Template recommended.** If the source is a template, clones are linked clones, which are
  fast. Otherwise they are full clones. Clones are made one at a time, because Proxmox locks
  the source while it clones. Provisioning the clones then runs in parallel (`--parallel`,
  default 4).

### Proxmox token privileges

Deploy and teardown can use a different API token from the broker. To do that, set
`Hotlab__Proxmox__TokenId`/`TokenSecret` when running the verbs. The token (with privilege
separation, so the token's own ACLs apply) needs:

| Privilege | Used for |
|---|---|
| `VM.Clone` (on the source) | `POST /nodes/{node}/qemu/{vmid}/clone` |
| `VM.Allocate` | creating clones (on `/vms/{newid}`, or on `/pool/hotlab-<name>`, since clones go straight into the pool) and destroying them |
| `VM.Audit` | reading the config (template flag, tags), status, and `/cluster/resources` |
| `VM.Config.Options` | adding the `hotlab` tag |
| `VM.PowerMgmt` | start, shutdown, stop |
| PVE 8: `VM.Monitor` / PVE 9: `VM.GuestAgent.Unrestricted` | agent `exec`, `exec-status` and `set-user-password` (on PVE 9 this privilege also covers `ping` and `get-osinfo`) |
| `Pool.Allocate`, `Pool.Audit` | creating, listing, inspecting and deleting the `hotlab-<name>` pool |
| `Datastore.AllocateSpace` | the clone's disks, on each target storage |
| `SDN.Use` | the clone's network bridges (`/sdn/zones/localnetwork/<bridge>`), PVE 8+ |

PVE 9 removed `VM.Monitor`, and `pve8to9` flags roles that still contain it, so define the
role for the version you run. Tags normally need only `VM.Config.Options`. They also need
`Sys.Modify` on `/` in two cases: `hotlab` is a datacenter "registered tag", or
`user-tag-access` is not the default (`free`).

Example for PVE 9. On PVE 8, replace `VM.GuestAgent.Unrestricted` with `VM.Monitor`:

```sh
pveum role add HotlabDeploy -privs "VM.Clone VM.Allocate VM.Audit VM.Config.Options VM.PowerMgmt \
  VM.GuestAgent.Unrestricted Pool.Allocate Pool.Audit Datastore.AllocateSpace SDN.Use"
pveum user add hotlab@pve
pveum user token add hotlab@pve deploy --privsep 1
pveum acl modify / -token 'hotlab@pve!deploy' -role HotlabDeploy   # or narrower paths
```

### Worked example

```sh
export Hotlab__Proxmox__Url=https://pve1.example.org:8006
export Hotlab__Proxmox__TokenId='hotlab@pve!deploy' Hotlab__Proxmox__TokenSecret=...
export Hotlab__Tokens__SigningKey=...   # the same key the gateway uses

Hotlab.Gateway deploy --source 9000 --node pve1 --address lab.example.org:3389 \
  --out ~/labs/net101 --name net101 --lifetime 120d \
  --users-file class.txt --users-file-target /etc/hotlab/users.json
```

For each user (lowercased, Linux username rules), this creates VM `hotlab-net101-<user>` in
pool `hotlab-net101`, tagged `hotlab`. It starts the VM, creates the account, sets a random
16-character password, and shuts the VM down again; the broker starts it when the user
connects. `--leave-running` skips the shutdown.

`~/labs/net101` (mode 0700) then contains:

- `<user>.rdp`: the gateway address, `username:s:<guest account>`, and a signed routing token
  in `loadbalanceinfo`. Use a long `--lifetime` for a lab that runs all term.
- `passwords.csv` (0600): `user,guest_account,password,vmid,rdp_file`.
- `users.json`: the `{"Hotlab":{"Users":{...}}}` mappings for the gateway.
- `deployment.json`: the manifest used by re-runs and teardown. It contains no secrets.

To have the gateway pick up mappings without a restart, point it at a users file with
`Hotlab:UsersFile` (or `HOTLAB_USERS_FILE`). The gateway loads that file with
reload-on-change. With `--users-file-target`, deploy merges into it atomically. Deploy refuses
to remap a user who is already mapped to a different VM, unless you pass `--force`.

Re-running the same command is safe:

- Users whose recorded VM still exists are skipped, and their rows, passwords and `.rdp` files
  are kept.
- New names are deployed.
- Failed users are retried.

A user who fails is recorded as `failed` and their partial clone is destroyed; the other users
carry on. The exit code is 1 if any user failed.

### Distributing passwords

The `.rdp` file cannot carry the password. mstsc stores it as `password 51:b:`, which is
encrypted with Windows DPAPI for one machine and user, so it can't be generated for someone
else. Hand out each user's `.rdp` file and their password from `passwords.csv` separately.

The routing token replaces the `mstshash` cookie: mstsc sends one or the other, never both. So
`username:s:` in the `.rdp` file only prefills the login prompt. That is why it holds the guest
account name.

### Teardown

```sh
Hotlab.Gateway teardown --out ~/labs/net101 [--keep-mappings]
```

Teardown stops and destroys each recorded VM, but only if it is still in the deployment's pool
**and** tagged `hotlab`. It then:

- deletes the pool once it is empty;
- removes the users' mappings from the users file (unless `--keep-mappings`), but only where
  they still point at this deployment's VMs;
- deletes those users' passwords. `passwords.csv` is removed once no VMs remain.

It is safe to re-run.
