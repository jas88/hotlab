# Hotlab

On-demand Proxmox VE lab VMs behind an RDP broker (`src/Hotlab.Gateway`). See
`docs/architecture.md` for the design.

## Deploying a lab (MVP)

`Hotlab.Gateway deploy` creates one clone of a source VM per user. Each clone gets a guest
account, a random password and its own `.rdp` file. `Hotlab.Gateway teardown` removes all of
it again. Both verbs use the gateway's normal configuration (`appsettings.json` plus
environment variables such as `Hotlab__Proxmox__TokenSecret` and `Hotlab__Tokens__SigningKey`).
The timings under `Hotlab:Deploy` and `Hotlab:Tokens` are validated before anything is cloned.

Exit codes: 0 on success (and for `--help`), 2 for a usage error, 1 for a configuration or
runtime error, including any user that failed.

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

Use a different API token for deploy and teardown from the one the gateway runs with (see
[Gateway configuration](#gateway-configuration)). Set `Hotlab__Proxmox__TokenId`/`TokenSecret`
when running the verbs. The deploy token (with privilege separation, so the token's own ACLs
apply) needs:

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

### Gateway configuration

- `Hotlab:Proxmox:Url` must be `https`, because the token is sent with every request.
  `Insecure` (accept any certificate, for test hosts only) and `Fingerprint` (pin the
  certificate) are mutually exclusive.
- `Hotlab:Gateway:MaxConnections` defaults to 256. Connections beyond it are closed. 0 means
  unlimited.
- The users file (`Hotlab:UsersFile`) supplies only `Hotlab:Users`. Any other keys in it are
  ignored.

**Security model.** In the MVP the gateway and broker are one process, so the process that
listens on 3389 holds a Proxmox token (see the MVP exception under "Component trust
boundaries" in `docs/architecture.md`). Give it a runtime token of its own, with only the
privileges the gateway needs to start a VM and find its address. Never give it the deploy
token:

| Privilege | Used for |
|---|---|
| `VM.Audit` | VM status |
| `VM.PowerMgmt` | start |
| PVE 8: `VM.Monitor` / PVE 9: `VM.GuestAgent.Audit` | agent `network-get-interfaces` (the VM's IP address; not needed for users mapped with a `Host`) |

Scope its ACL to the Hotlab pools, for example `/pool/hotlab-net101`.

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

Deployment names (`--name`, or `--out`'s directory name) longer than 32 characters are
rejected. A VM name that would exceed 63 characters is shortened and given a short hash
suffix, so two users never share a name.

If `--out` does not exist, it is created with mode 0700. The permissions of an existing
directory are not changed; deploy only warns if group or others can access it.
`~/labs/net101` then contains:

- `<user>.rdp`: the gateway address, `username:s:<guest account>`, and a signed routing token
  in `loadbalanceinfo`. Use a long `--lifetime` for a lab that runs all term.
- `passwords.csv` (0600): `user,guest_account,password,vmid,rdp_file`.
- `users.json`: the `{"Hotlab":{"Users":{...}}}` mappings for the gateway.
- `deployment.json`: the manifest used by re-runs and teardown. It contains no secrets.

To have the gateway pick up mappings without a restart, point it at a users file with
`Hotlab:UsersFile` (or `HOTLAB_USERS_FILE`). The gateway loads that file with
reload-on-change. With `--users-file-target`, deploy merges into it atomically, holding a lock
on `<users file>.lock`, which is created next to it. Deploy refuses to remap a user who is
already mapped to a different VM, unless you pass `--force`. A malformed entry for a user is
overwritten with a warning. Once a deployment has merged into a users file, it cannot be
moved to another one, even with `--force`. Tear it down first.

Re-running the same command is safe:

- Users whose recorded VM still exists are skipped, and their rows, passwords and `.rdp` files
  are kept.
- New names are deployed.
- Failed users are retried.
- A different `--address` is refused unless you pass `--force`. Then every `.rdp` file is
  regenerated with a fresh token.

A user who fails is recorded as `failed`, and the other users carry on. The exit code is 1 if
any user failed. Deploy then tries to destroy the user's partial clone, but only if it is still
in the deployment's pool with the expected name. Cleanup is best-effort. If that ownership
check fails, or Proxmox returns an error, the clone is left in place and deploy prints
`clean it up manually` (or `not destroying it`). To find leftovers, list pool
`hotlab-<name>`: clones go straight into it, and are tagged `hotlab` once the clone finishes.
Any member that `deployment.json` does not record as deployed is a leftover.

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
- deletes those users' passwords and `.rdp` files. `passwords.csv` is removed once no VMs
  remain.

A VM it could not destroy (not ours any more, or a Proxmox error) keeps its row in
`deployment.json` and its password and `.rdp` file, and teardown exits 1. It is safe to re-run.

## Installing from the .deb

Each [release](https://github.com/jas88/hotlab/releases) has `hotlab-gateway_<version>_amd64.deb`
and `_arm64.deb` for Debian 12 and 13, a source tarball and `SHA256SUMS`. (Every CI run also
uploads them as workflow artifacts.) The binary is a self-contained .NET build, so no .NET
runtime is needed.

```sh
sha256sum --check --ignore-missing SHA256SUMS
sudo apt install ./hotlab-gateway_1.2.3_amd64.deb
```

The package installs:

| Path | What |
|---|---|
| `/usr/bin/hotlab-gateway` | the gateway and its `mint-rdp`/`deploy`/`teardown` verbs (`man hotlab-gateway`) |
| `/usr/lib/hotlab-gateway/appsettings.json` | defaults; replaced on upgrade, don't edit |
| `/etc/hotlab/appsettings.json` | site settings (a conffile: your edits survive upgrades) |
| `/etc/hotlab/users.json` | user map, created empty on first install and maintained by `deploy` |
| `hotlab-gateway.service` | the systemd unit, which runs as the `hotlab` system user |

`/etc/hotlab` is `root:hotlab`, mode 2750. The service can read it but not write to it.
Because the directory is setgid, files that root writes there (for example `deploy` updating
`users.json`) stay readable by the service.

Settings are read in this order, later sources winning: the defaults, then
`/etc/hotlab/appsettings.json` (`$HOTLAB_CONFIG_DIR`), then systemd credentials, then
environment variables (`Hotlab__Section__Key`).

### Configure

1. Edit `/etc/hotlab/appsettings.json`: set `Hotlab:Proxmox:Url`, the runtime `TokenId`
   (which needs only `VM.Audit`, `VM.PowerMgmt` and agent read access), and `Fingerprint` if
   the PVE certificate isn't publicly trusted. Don't put secrets in this file.
2. Encrypt the two secrets with `systemd-creds`. This uses the TPM2 if there is one;
   otherwise (in an LXC, for example) it uses the host key in `/var/lib/systemd/credential.secret`.

   ```sh
   sudo install -d -m 0700 /etc/credstore.encrypted
   # Paste the Proxmox token secret and press Ctrl-D:
   sudo systemd-creds encrypt --name=Hotlab__Proxmox__TokenSecret - \
       /etc/credstore.encrypted/Hotlab__Proxmox__TokenSecret
   # A new routing-token signing key (32 random bytes, base64):
   head -c 32 /dev/urandom | base64 | sudo systemd-creds encrypt \
       --name=Hotlab__Tokens__SigningKey - /etc/credstore.encrypted/Hotlab__Tokens__SigningKey
   ```

3. Load the secrets into the service. Run `sudo systemctl edit hotlab-gateway` and add:

   ```ini
   [Service]
   LoadCredentialEncrypted=Hotlab__Proxmox__TokenSecret:/etc/credstore.encrypted/Hotlab__Proxmox__TokenSecret
   LoadCredentialEncrypted=Hotlab__Tokens__SigningKey:/etc/credstore.encrypted/Hotlab__Tokens__SigningKey
   ```

   systemd decrypts the secrets into a private `$CREDENTIALS_DIRECTORY` that only the service
   can read. The gateway reads each file there as a setting, with `__` in the name standing
   for `:`. The secrets never appear in the environment.

### Start and logs

The package enables the service but doesn't start it, because it can't run until it's
configured:

```sh
sudo systemctl restart hotlab-gateway
systemctl status hotlab-gateway
journalctl -u hotlab-gateway -f
```

A bad or missing setting stops the service at startup, and the journal names the setting.
The service binds port 3389 as `hotlab` through `CAP_NET_BIND_SERVICE` and is sandboxed: the
whole file system is read-only to it.

### Deploying from the installed package

Run `deploy` and `teardown` as root, not as the `hotlab` user: they update
`/etc/hotlab/users.json`, which `hotlab` can only read. They read
`/etc/hotlab/appsettings.json` like the service does. The service's credentials are not available to these commands, so give them the
**deploy** token (which has the clone, destroy and agent privileges listed above) and the
signing key separately. One way is to run the command in a transient unit that loads
encrypted credentials, so the secrets stay out of the environment:

```sh
# Once: store the deploy token's secret next to the others.
sudo systemd-creds encrypt --name=Hotlab__Proxmox__TokenSecret - \
    /etc/credstore.encrypted/hotlab-deploy-token-secret

sudo systemd-run --pipe --wait --collect --same-dir \
    -p LoadCredentialEncrypted=Hotlab__Proxmox__TokenSecret:/etc/credstore.encrypted/hotlab-deploy-token-secret \
    -p LoadCredentialEncrypted=Hotlab__Tokens__SigningKey:/etc/credstore.encrypted/Hotlab__Tokens__SigningKey \
    -E Hotlab__Proxmox__TokenId='hotlab@pve!deploy' \
    hotlab-gateway deploy --source 9000 --node pve1 --address lab.example.org:3389 \
      --out /root/labs/net101 --name net101 --users-file class.txt \
      --users-file-target /etc/hotlab/users.json
```

Simpler, but the secrets go into the process environment:
`sudo env Hotlab__Proxmox__TokenId='hotlab@pve!deploy' Hotlab__Proxmox__TokenSecret=... Hotlab__Tokens__SigningKey="$(sudo systemd-creds decrypt /etc/credstore.encrypted/Hotlab__Tokens__SigningKey -)" hotlab-gateway deploy ...`.
To use a different settings directory, set `HOTLAB_CONFIG_DIR`. The running gateway picks up
`users.json` changes without a restart.

### Upgrade, remove, purge

- **Upgrade** (`apt install` a newer `.deb`): your `/etc/hotlab/appsettings.json` edits are
  kept. dpkg asks before replacing a modified conffile. `users.json` and the credentials are
  never touched. If the service was running, it is restarted once the new version is
  installed; if it was stopped, it stays stopped.
- **Remove** (`apt remove hotlab-gateway`) stops the service and deletes the program but
  keeps `/etc/hotlab` and the `hotlab` account.
- **Purge** (`apt purge hotlab-gateway`) also deletes `/etc/hotlab`, including `users.json`.
  It leaves `/etc/credstore.encrypted` and the `hotlab` account in place; remove those by hand
  if you no longer need them.

## License

Hotlab is licensed under the GNU Affero General Public License v3.0 or later
(AGPL-3.0-or-later); see [LICENSE](LICENSE). If you run a modified version as a
network service, section 13 requires offering its source to the users who interact
with it.
