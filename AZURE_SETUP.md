# Azure multiplayer setup

## Recommended starting architecture

**One .NET 10 authoritative WebSocket server in Azure Container Apps Consumption, serving all three lobby tables.** Unity clients connect directly to its `wss://.../ws` endpoint. Azure terminates TLS; the process listens on port 8080. There is no Unity dedicated-server license/runtime on Azure and no client hosting the table.

This is a practical low-complexity starting point for a small private group. The authoritative state is tiny; no Azure SignalR, Redis, SQL database, PlayFab, AKS cluster, load balancer, or virtual machine is required. The shared C# core runs on the server; Unity only displays approved state and sends player commands. .NET 10 is LTS through November 2028.

## What to create

| Resource or setting | Purpose | Starting configuration |
| --- | --- | --- |
| Azure subscription + resource group | Billing boundary and deployment permissions | Dedicated group such as `drawmaha-rg` |
| Container Apps environment | Managed hosting/network boundary | Consumption workload; region near your players, e.g. `centralus` |
| Container App | Runs the game server | 0.25 vCPU / 0.5 GiB; min replicas **1**; max replicas **1**; **single revision** |
| External HTTP ingress | Secure WebSocket endpoint | Target port **8080**, HTTP transport, HTTPS/TLS required; public 443 |
| Azure Container Registry | Private image storage and remote Docker builds | Basic SKU; globally unique registry name |
| User-assigned managed identity + `AcrPull` role | Pull the container without registry passwords | Scope role to that registry |
| Container App secret `invite-key` | Restrict access to your private group | 16+ characters; reference as `DRAWMAHA_ACCESS_KEY` |
| Cost Management budget | Spending visibility | Set an alert suitable for your budget; this is an alert, not a hard stop |

The included script disables central log collection to avoid another recurring service. Live console logs remain available for debugging. Add a Log Analytics workspace later if persistent operational logs are useful. Do not enable frame/body logging: it would capture credentials and private cards.

Azure's HTTP ingress already supports WebSockets and TLS. You do not need to purchase a domain or certificate; use the assigned `azurecontainerapps.io` hostname. If you later use built-in Azure authentication, the Unity transport also needs an identity/token flow; this version uses a private invite key and resumable guest tokens instead.

## Costs

Container Apps Consumption bills allocated CPU/memory duration and requests. Its subscription-wide monthly grants are 180,000 vCPU-seconds, 360,000 GiB-seconds, and 2 million requests. At 0.25 vCPU / 0.5 GiB, those CPU and memory grants each correspond to **200 fully active replica-hours**, if the grants are not consumed elsewhere. This is a calculation from the published grants, not a guarantee of free hosting.

Keep **min = max = 1** for this in-memory version. The replica may qualify for lower idle rates when unused, but WebSocket activity and processing can cause active billing. It is intentionally not configured to scale to zero because that discards running tables. A minimum replica does not prevent crashes, maintenance, or restart-related resets.

Budget for the Container App **plus** Basic registry storage, image-build charges, and outbound data. Persisted logs would add costs if enabled. Prices vary by region, currency, and usage; check the [Azure pricing calculator](https://azure.microsoft.com/en-us/pricing/calculator/) with your region before creating resources. An existing external container registry can replace ACR if you want to avoid an additional Azure registry bill; the included path uses ACR for simpler Azure builds and managed-identity pulls. Do not enable a Dedicated workload profile for this small deployment.

## Deploy from Windows PowerShell

1. Install the current **Azure CLI** and **.NET 10 SDK**. Sign in with `az login`; select the intended subscription with `az account set --subscription <subscription-id>`.
2. You need permission to create resources **and** assign `AcrPull`. Subscription Owner works; otherwise use Contributor plus suitable role-assignment permission (e.g. User Access Administrator). The registry name must be globally unique, lowercase alphanumeric, 5–50 characters.
3. From this project folder, run:

   ```powershell
   .\Azure\deploy.ps1 -RegistryName skipdrawmaha2026 -ResourceGroupName drawmaha-rg -Location centralus
   ```

   Replace the registry example with your own available name. The script prompts securely for an invite key; it creates billable resources only when **you run it**. It builds the image remotely with `az acr build`; Docker Desktop is not required for that route. If your subscription does not allow ACR Tasks/builds, build `Server/Dockerfile` locally with Docker, tag it for your registry, and push it instead.
4. The script prints a `wss://<hostname>/ws` client address and `https://<hostname>/health` health URL. Check that health returns `{"status":"ok","protocol":3}`.
5. Configure the generated `wss://` address as the client's default endpoint in `Assets/Drawmaha/Runtime/TableView.cs`, then build and distribute the client. Players enter the invite key and different usernames on the connect screen; the lobby appears after connecting. The endpoint is no longer loaded from PlayerPrefs, so the configured build default is always used. The invite is not saved in the distributed game or PlayerPrefs; reconnect tokens are saved locally per username/server.
6. Verify the Azure portal shows Consumption, min/max 1, single revision, target port 8080, and insecure ingress disabled. Set your budget alert.

Azure role assignments can take time to propagate. If the image pull fails immediately after creating `AcrPull`, verify the role scope and managed identity, wait for propagation, and retry the Container App creation. The script stops on errors and refuses to replace an already-running app.

## Runtime settings

| Variable | Default | Meaning |
| --- | ---: | --- |
| `DRAWMAHA_ACCESS_KEY` | Empty in Development | Production requires 16+ characters |
| `DRAWMAHA_ACTION_SECONDS` | 30 | Time to act; server clamps to 2–120 seconds |
| `DRAWMAHA_SHOWDOWN_SECONDS` | 6 | Delay before next hand; 1–30 seconds |
| `DRAWMAHA_RECONNECT_SECONDS` | 90 | Seat reservation while offline; 2–600 seconds |
| `ASPNETCORE_HTTP_PORTS` | 8080 in container | Backend listener |

## Updates, restart, and scaling

**All game/session state is currently in memory.** Restarting or deploying a new image resets tables, buy-ins, and reconnect identities. There is no durable wallet/account database. The client recognizes a new server epoch and returns to the lobby. Drain tables and announce a maintenance window before upgrades.

After all tables are empty, run the updater from the project root. It builds a uniquely tagged image, asks you to type `EMPTY` before restarting the app, then checks the health endpoint. It requires an Azure CLI login and the existing registry/app names:

```powershell
.\Azure\update.ps1 -RegistryName <registry-name>
```

Pass `-ResourceGroupName` or `-AppName` if you used non-default names. The script does not verify that tables are empty; check this in the client before confirming. To adjust health polling, use `-HealthCheckAttempts` and `-HealthCheckIntervalSeconds`.

Do not turn on multiple replicas or multiple live revisions. Two processes would create independent copies of the same tables; session affinity alone does not solve that. A future scale-out version needs explicit table ownership/routing and durable recovery/checkpoints. For private play, start with one replica and measure utilization before increasing its CPU/memory.

## Validation boundary

The server was built and tested locally using multiple real WebSocket connections. Azure resources have **not** been provisioned, the deployment script has not been executed against a subscription, and the Docker image has not been run here. Unity Editor rendering/builds still need verification on your computer.

## Official references checked

- [Container Apps ingress: WebSockets and TLS](https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview)
- [Consumption billing, grants, and idle rates](https://learn.microsoft.com/en-us/azure/container-apps/billing)
- [Container Apps pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/)
- [Azure CLI Container App commands](https://learn.microsoft.com/en-us/cli/azure/containerapp)
- [.NET support policy](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)
