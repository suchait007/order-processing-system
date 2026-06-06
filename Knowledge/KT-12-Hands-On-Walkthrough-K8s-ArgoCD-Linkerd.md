# KT-12: Hands-On Walkthrough — K8s + Kustomize + ArgoCD + Linkerd

> **Project**: Order Processing System  
> **Date**: 2026-06-06  
> **Scope**: Complete end-to-end setup from Docker Compose → K8s → GitOps → Service Mesh

---

## 1. Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│                        GitHub (Git Repo)                        │
│  suchait007/order-processing-system                             │
│  └── k8s/overlays/dev/  ← ArgoCD watches this path             │
└──────────────────────────┬───────────────────────────────────────┘
                           │ HTTPS pull (every 3min or webhook)
                           ▼
┌──────────────────────────────────────────────────────────────────┐
│                    ArgoCD (argocd namespace)                     │
│  Application: order-system-dev                                  │
│  syncPolicy: automated, selfHeal: true, prune: true             │
│  Compares Git desired state vs cluster live state                │
└──────────────────────────┬───────────────────────────────────────┘
                           │ kubectl apply
                           ▼
┌──────────────────────────────────────────────────────────────────┐
│              Linkerd Control Plane (linkerd namespace)           │
│  identity │ destination │ proxy-injector                        │
│  Injects sidecar proxy into app pods automatically              │
└──────────────────────────┬───────────────────────────────────────┘
                           │ mTLS sidecar injection
                           ▼
┌──────────────────────────────────────────────────────────────────┐
│                order-system namespace                            │
│                                                                  │
│  ┌─────────────┐   HTTP    ┌──────────────┐  Kafka   ┌────────┐│
│  │  StoreApi    │◄─────────│ OrderService  │────────►│ Kafka   ││
│  │  (2/2 mesh)  │          │  (2/2 mesh)   │         │ (1/1)   ││
│  └──────┬───────┘          └──────┬────────┘         └────┬────┘│
│         │                         │                       │     │
│    ┌────▼────┐   ┌────────┐  ┌───▼──────┐  ┌────────────▼───┐ │
│    │SQLServer│   │  Redis  │  │PostgreSQL│  │InventoryWorker │ │
│    │ (1/1)   │   │  (1/1)  │  │  (1/1)   │  │  (2/2 mesh)    │ │
│    └─────────┘   └────────┘  └──────────┘  └────────────────┘ │
│                                                                  │
│  (2/2) = app container + linkerd-proxy sidecar                  │
│  (1/1) = infra container only (no sidecar — binary protocols)   │
└──────────────────────────────────────────────────────────────────┘
```

---

## 2. Layer-by-Layer Breakdown

### Layer 1: Kubernetes Manifests (`k8s/base/`)

**What**: Raw K8s YAML that describes your desired cluster state.

**Directory structure**:
```
k8s/
├── base/
│   ├── kustomization.yaml     # Root — sets namespace, includes sub-dirs
│   ├── namespace.yaml         # Namespace + Linkerd injection annotation
│   ├── infra/                 # Infrastructure services
│   │   ├── kustomization.yaml
│   │   ├── kafka.yaml         # Deployment + Service
│   │   ├── zookeeper.yaml
│   │   ├── redis.yaml
│   │   ├── postgres.yaml
│   │   ├── sqlserver.yaml
│   │   └── secrets.yaml       # DB passwords
│   └── apps/                  # Application services
│       ├── kustomization.yaml
│       ├── store-api.yaml
│       ├── order-service.yaml
│       ├── inventory-worker.yaml
│       ├── configmap.yaml     # Kafka/Redis URLs, thresholds
│       ├── secrets.yaml       # Connection strings
│       └── store-api-serviceprofile.yaml  # Linkerd per-route metrics
├── overlays/
│   ├── dev/kustomization.yaml
│   └── prod/kustomization.yaml
└── argocd-application.yaml    # ArgoCD Application CRD
```

**Key concepts demonstrated**:
- **Deployments** — declarative pod management with replicas, health checks
- **Services** — ClusterIP for internal DNS (`kafka:9092`, `store-api:5116`)
- **ConfigMaps** — externalized config (URLs, thresholds)
- **Secrets** — base64-encoded credentials
- **imagePullPolicy: Never** — for locally-loaded images in Kind

**Critical gotcha — Kafka in K8s**:
Confluent's `cp-kafka:7.7.1` image has a bug where the `configure` script doesn't write
environment variables to `server.properties`, and `launch` expects `kafka.properties` 
which doesn't exist. Fix: generate `kafka.properties` manually in the container command:

```yaml
command: ["bash", "-c", "cat > /etc/kafka/kafka.properties <<EOF\nbroker.id=0\n...EOF\nexec /etc/confluent/docker/launch"]
```

---

### Layer 2: Kustomize (`k8s/overlays/`)

**What**: Patch-based configuration management. No templates, no new syntax — just patches on top of base YAML.

**How it works**:
```
base/                     overlays/dev/              overlays/prod/
├── apps/                 ├── kustomization.yaml     ├── kustomization.yaml
│   └── order-service     │   replicas: 1            │   replicas: 3
│       replicas: ???     │   image: v1              │   image: v1.0.0
│       image: ???        │   resources: 256Mi       │   resources: 512Mi
```

**Dev overlay** (`k8s/overlays/dev/kustomization.yaml`):
```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
resources:
  - ../../base
replicas:
  - name: store-api
    count: 1
images:
  - name: order-system/store-api
    newTag: v1
commonAnnotations:
  environment: dev
```

**Key concepts**:
- `resources: [../../base]` — inherits everything from base
- `replicas` — override replica counts per environment
- `images` — pin image tags without editing base
- `commonAnnotations` — stamp environment labels on all resources

**Why not Helm?**
- Kustomize: no templates, pure YAML, built into `kubectl`
- Helm: Go templates, package manager, charts
- For learning: Kustomize teaches K8s fundamentals; Helm abstracts them away

---

### Layer 3: GitOps with ArgoCD

**What**: Git is the single source of truth. ArgoCD continuously reconciles 
cluster state with what's declared in Git.

**The GitOps flow**:
```
1. Developer changes YAML in Git
2. git push origin main
3. ArgoCD detects change (polls every 3 min or webhook)
4. ArgoCD compares Git state vs cluster state
5. ArgoCD applies the diff (kubectl apply)
6. If someone manually changes the cluster → ArgoCD reverts it (self-heal)
```

**ArgoCD Application CRD** (`k8s/argocd-application.yaml`):
```yaml
apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
  name: order-system-dev
  namespace: argocd
spec:
  project: default
  source:
    repoURL: https://github.com/suchait007/order-processing-system.git
    targetRevision: main
    path: k8s/overlays/dev
  destination:
    server: https://kubernetes.default.svc
    namespace: order-system
  syncPolicy:
    automated:
      prune: true       # Delete resources removed from Git
      selfHeal: true     # Revert manual cluster changes
    syncOptions:
      - CreateNamespace=true
```

**Key fields**:
- `source.path` — ArgoCD runs `kustomize build` on this directory
- `syncPolicy.automated` — no manual approval needed (dev environment)
- `selfHeal: true` — if someone does `kubectl scale --replicas=5`, ArgoCD reverts to Git value
- `prune: true` — if you delete a file from Git, ArgoCD deletes it from the cluster

**Self-heal demonstration (we tested this)**:
```
1. kubectl scale deployment order-service --replicas=5 -n order-system
   → Pods scale to 5
2. Wait 30 seconds...
   → ArgoCD detects drift from Git (Git says 2)
   → ArgoCD scales back to 2
```

**Important: Local K8s + GitHub works perfectly**  
ArgoCD makes **outbound** HTTPS requests to GitHub. No inbound access needed.
Your K8s can be behind a firewall — as long as it can reach github.com, GitOps works.

---

### Layer 4: Linkerd Service Mesh

**What**: Transparent layer that adds mTLS, observability, and reliability 
to pod-to-pod communication without changing application code.

**How it works**:
```
Pod without Linkerd:              Pod with Linkerd:
┌────────────────┐                ┌────────────────────────────┐
│  App Container │                │  App Container │  Proxy    │
│  (port 5116)   │                │  (port 5116)   │  Sidecar  │
└────────────────┘                │                │  (mTLS)   │
                                  └────────────────────────────┘
                                  Pod shows 2/2 READY (2 containers)
```

**Installation sequence**:
```
1. kubectl apply -f gateway-api-crds.yaml      # Prerequisite
2. linkerd install --crds | kubectl apply -f -  # Linkerd CRDs
3. linkerd install | kubectl apply -f -         # Control plane
4. linkerd viz install | kubectl apply -f -     # Dashboard + Prometheus
```

**PowerShell gotcha**: `linkerd install --crds | kubectl apply -f -` fails because
stderr gets mixed into stdout. Fix: save to file first, then apply:
```powershell
$crds = linkerd install --crds 2>$null
$crds | Out-File -FilePath crds.yaml -Encoding utf8
kubectl apply -f crds.yaml
```

**Kind gotcha**: Kind nodes can't pull from `cr.l5d.io`. Must load images manually:
```powershell
docker pull cr.l5d.io/linkerd/controller:edge-26.6.1
docker save -o controller.tar cr.l5d.io/linkerd/controller:edge-26.6.1
docker cp controller.tar desktop-worker:/root/controller.tar
docker exec desktop-worker ctr -n k8s.io images import /root/controller.tar
```

**Injection via GitOps**:
Instead of manually running `linkerd inject`, we added an annotation to namespace.yaml:
```yaml
metadata:
  name: order-system
  annotations:
    linkerd.io/inject: enabled    # ← This one line meshes ALL pods
```

Then pushed to Git → ArgoCD synced → pods restarted → sidecars injected.

**Critical: Selective injection**  
Binary protocol services (Kafka, Redis, PostgreSQL, SQL Server, Zookeeper) must 
opt-out because Linkerd's proxy is HTTP-aware and breaks non-HTTP traffic:
```yaml
# In each infra deployment's pod template:
template:
  metadata:
    annotations:
      linkerd.io/inject: disabled   # Skip sidecar for this pod
```

**Result**: App pods get sidecars (2/2), infra pods don't (1/1).

---

### Layer 5: ServiceProfile (Per-Route Metrics)

**What**: Tells Linkerd about your API routes so it can track metrics per endpoint.

```yaml
apiVersion: linkerd.io/v1alpha2
kind: ServiceProfile
metadata:
  name: store-api.order-system.svc.cluster.local
spec:
  routes:
    - name: GET /api/products/{id}
      condition:
        method: GET
        pathRegex: /api/products/[^/]+
```

**Viewing route metrics**:
```
$ linkerd viz routes deploy/store-api -n order-system

ROUTE                       SERVICE    SUCCESS    RPS    LATENCY_P50
GET /api/products/{id}      store-api  100.00%    0.1rps  8ms
```

**Viewing service edges** (who talks to whom):
```
$ linkerd viz edges deploy -n order-system

SRC              DST           SECURED
order-service    store-api     √         ← mTLS encrypted
inventory-worker kafka         Not Provided  ← infra not meshed
```

---

## 3. Environment Details

| Component | Details |
|-----------|---------|
| K8s | Docker Desktop with Kind (v1.34.3) |
| Nodes | `desktop-control-plane` + `desktop-worker` |
| Linkerd | edge-26.6.1 |
| ArgoCD | v2.x (latest stable) |
| Git | github.com/suchait007/order-processing-system |
| Context | `docker-desktop` at `https://127.0.0.1:58690` |

**Namespaces**:
- `order-system` — your application (8-9 pods)
- `argocd` — ArgoCD control plane (7 pods)
- `linkerd` — Linkerd control plane (3 pods)
- `linkerd-viz` — Dashboard, Prometheus, Tap (5 pods)

---

## 4. Commands Reference

### Kustomize
```bash
kubectl kustomize k8s/overlays/dev/      # Preview rendered YAML
kubectl apply -k k8s/overlays/dev/       # Apply overlay
```

### ArgoCD
```bash
# Install
kubectl apply --server-side=true --force-conflicts -f install.yaml -n argocd

# Get admin password
kubectl -n argocd get secret argocd-initial-admin-secret \
  -o jsonpath="{.data.password}" | base64 --decode

# Port-forward UI
kubectl port-forward svc/argocd-server -n argocd 8443:443

# Force sync
kubectl annotate application order-system-dev -n argocd \
  argocd.argoproj.io/refresh=normal --overwrite

# Check status
kubectl get application -n argocd
```

### Linkerd
```bash
# Install
linkerd check --pre          # Pre-flight checks
linkerd install --crds       # CRDs
linkerd install              # Control plane
linkerd viz install           # Observability
linkerd check                # Verify installation

# Observe
linkerd viz stat deploy -n order-system        # Golden metrics
linkerd viz routes deploy/store-api -n order-system  # Per-route
linkerd viz edges deploy -n order-system       # Service graph
linkerd viz top deploy/order-service -n order-system  # Live traffic

# Dashboard
linkerd viz dashboard        # Opens browser
```

### GitOps Workflow
```bash
# The pattern is always:
1. Edit YAML files locally
2. git add → git commit → git push
3. ArgoCD auto-syncs (or force: kubectl annotate ... refresh=normal)
4. Verify: kubectl get pods / linkerd viz stat
```

---

## 5. Gotchas & Lessons Learned

| Problem | Root Cause | Fix |
|---------|-----------|-----|
| Docker save pipe fails in PowerShell | Tar corruption in PS pipe | Use file-based: `docker save -o file.tar` |
| Kind can't pull images | No internet from Kind nodes | Pull locally → save → docker cp → ctr import |
| `/tmp` files vanish on Kind nodes | tmpfs mount | Use `/root/` instead |
| Kafka `configure` script broken | cp-kafka 7.7.1 bug | Generate `kafka.properties` manually |
| ArgoCD CRD apply fails | Annotation > 262KB | `kubectl apply --server-side=true --force-conflicts` |
| `linkerd install \| kubectl apply` fails | PS merges stderr into stdout | Save to file first: `linkerd install 2>$null \| Out-File` |
| Kafka/Redis break with Linkerd sidecar | Binary protocols ≠ HTTP proxy | `linkerd.io/inject: disabled` on infra pods |
| Route metrics show dashes | External traffic bypasses proxy | Only in-mesh (pod-to-pod) traffic is tracked |

---

## 6. What Each Layer Gives You

| Layer | What It Adds | Value |
|-------|-------------|-------|
| **K8s** | Container orchestration | Self-healing, scaling, service discovery |
| **Kustomize** | Environment overlays | Same base, different configs per env |
| **GitOps/ArgoCD** | Declarative deployments | Audit trail, rollback, no `kubectl apply` in prod |
| **Linkerd** | Service mesh | Zero-config mTLS, golden metrics, traffic visibility |

---

## 7. Next Steps to Explore

1. **Linkerd retries & timeouts** — Add retry budgets to ServiceProfile
2. **ArgoCD Rollbacks** — `kubectl argo rollouts` for canary/blue-green
3. **Multi-cluster** — Linkerd multi-cluster for cross-cluster mTLS
4. **Sealed Secrets** — Encrypt secrets in Git (Bitnami Sealed Secrets)
5. **Notifications** — ArgoCD notifications to Slack/Teams on sync events
6. **Helm** — Compare with Kustomize for more complex applications
7. **Production hardening** — Resource limits, PDBs, network policies, HPA
