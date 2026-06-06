# KT-07 — Learning Roadmap: K8s, Kustomize, Linkerd, ArgoCD & GitOps

## Case Study: Your Order Processing System on Kubernetes

This roadmap uses **your 3 services** (StoreApi, OrderService, InventoryWorker) as the running
case study throughout every phase. Each phase builds on the previous one — don't skip ahead.

```
┌──────────────────────────────────────────────────────────────────────┐
│                    YOUR SYSTEM ARCHITECTURE                         │
│                                                                     │
│   Browser/Postman                                                   │
│        │                                                            │
│        ▼                                                            │
│   ┌──────────┐   HTTP    ┌──────────────┐   Kafka     ┌──────────┐ │
│   │ StoreApi  │◄─────────│ OrderService │────────────►│Inventory │ │
│   │ (SQL Svr) │ get prod │  (Postgres)  │ order.placed│ Worker   │ │
│   └──────────┘  details  └──────────────┘             │(SQL Svr) │ │
│                               │                        └──────────┘ │
│                               │ cache                               │
│                               ▼                                     │
│                           ┌───────┐                                 │
│                           │ Redis │                                 │
│                           └───────┘                                 │
└──────────────────────────────────────────────────────────────────────┘
```

---

## PHASE 1 — Kubernetes Fundamentals (Week 1-2)

### What You're Learning
Core K8s objects and how docker-compose concepts map to them.

### Concept Map: Docker Compose → Kubernetes

| docker-compose.yml         | Kubernetes Equivalent       | Why it matters                    |
|----------------------------|-----------------------------|-----------------------------------|
| `services:`                | `Deployment`                | Manages pod replicas & rollouts   |
| `ports: "5200:5200"`       | `Service` (ClusterIP)       | Stable DNS name inside cluster    |
| `ports:` (host-exposed)    | `Service` (NodePort/LB)     | Expose outside cluster            |
| `environment:`             | `ConfigMap` + `Secret`      | Decouple config from image        |
| `volumes:`                 | `PersistentVolumeClaim`     | Survive pod restarts              |
| `depends_on:`              | `initContainers` / probes   | No equivalent — K8s is declarative|
| `build: context`           | `docker build` + `kind load`| K8s doesn't build, it pulls       |
| `container_name:`          | Pod name (auto-generated)   | K8s manages names for you         |

### Exercises (Do These In Order)

#### 1.1 — Deploy Redis to K8s (Simplest workload)
**Goal:** Understand Deployment, Service, Pod lifecycle

```bash
# Create a namespace (like a folder for your resources)
kubectl create namespace order-system

# Create your first deployment
kubectl create deployment redis --image=redis:7-alpine -n order-system

# Explore what K8s created
kubectl get pods -n order-system                    # See the pod
kubectl describe pod <pod-name> -n order-system     # See events, status
kubectl logs <pod-name> -n order-system             # See Redis startup logs

# Expose it as a service (ClusterIP = internal only)
kubectl expose deployment redis --port=6379 -n order-system

# Test connectivity from inside the cluster
kubectl run redis-test --rm -it --image=redis:7-alpine -n order-system -- redis-cli -h redis ping
# Should print: PONG
```

**Key Takeaway:** `redis.order-system.svc.cluster.local` is the DNS name.
Your docker-compose used `redis:6379` — K8s uses the same pattern but with full DNS.

#### 1.2 — Write Your First YAML Manifest
**Goal:** Move from imperative (`kubectl create`) to declarative (YAML files)

Write `redis.yaml` by hand (don't copy-paste — type it to learn):
```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: redis
  namespace: order-system
spec:
  replicas: 1
  selector:
    matchLabels:
      app: redis
  template:                     # ← This is the Pod template
    metadata:
      labels:
        app: redis
    spec:
      containers:
        - name: redis
          image: redis:7-alpine
          ports:
            - containerPort: 6379
```

```bash
# Delete what we created imperatively, apply declaratively
kubectl delete deployment redis -n order-system
kubectl apply -f redis.yaml

# See what changed
kubectl get all -n order-system
```

**Case Study Connection:** This is exactly what your `docker-compose.yml` Redis section
becomes in K8s. Notice: no `depends_on` — K8s handles readiness differently (probes).

#### 1.3 — Deploy StoreApi to Kind
**Goal:** Load your own Docker images into Kind, configure connection strings

```bash
# Build your image locally
docker build -t order-system/store-api:v1 ../StoreApi/

# Load it into Kind (Kind can't pull from local Docker daemon directly)
kind load docker-image order-system/store-api:v1 --name <your-cluster-name>

# Deploy SQL Server first (StoreApi needs it)
# Write sqlserver.yaml with env vars from your docker-compose
# Then deploy StoreApi pointing to sqlserver service
```

**Things to figure out (this is the learning):**
- How do you pass `ConnectionStrings__DefaultConnection` ? → `ConfigMap` or `Secret`
- How does StoreApi find SQL Server? → `sqlserver.order-system.svc.cluster.local,1433`
- What if SQL Server isn't ready yet? → `readinessProbe` + `livenessProbe`
- How do you access StoreApi from your browser? → `kubectl port-forward`

#### 1.4 — Deploy the Full System
**Goal:** Get all 3 services + infra running in K8s

Deploy in this order (matching your docker-compose dependency chain):
1. `redis` + `postgres` + `sqlserver` + `zookeeper` → `kafka`
2. `store-api` (needs sqlserver)
3. `order-service` (needs postgres, kafka, redis, store-api)
4. `inventory-worker` (needs sqlserver, kafka, redis)

```bash
# Verify the full flow works
kubectl port-forward svc/order-service 5200:5200 -n order-system
# Use your Postman collection to place an order
# Check inventory-worker logs to see it consumed the Kafka message
kubectl logs -f deployment/inventory-worker -n order-system
```

### Key Concepts to Understand Before Moving On
- [ ] What is a Pod vs Deployment vs ReplicaSet?
- [ ] How does K8s DNS work? (service-name.namespace.svc.cluster.local)
- [ ] What's the difference between ClusterIP, NodePort, LoadBalancer?
- [ ] How do readiness vs liveness probes work?
- [ ] What happens when you `kubectl delete pod <name>`? (it comes back!)
- [ ] How do Secrets and ConfigMaps inject environment variables?

---

## PHASE 2 — Kustomize (Week 3)

### What You're Learning
How to manage **different configurations for different environments** without duplicating YAML.

### Why Kustomize Matters for Your System

Right now your docker-compose has hardcoded passwords like `ChangeMeNow123!`.
In production, you'd want:
- Different passwords
- Different resource limits (more CPU/memory)
- Different replica counts
- Maybe different image tags

Kustomize solves this with **base + overlays** (no templating, just patching).

### Concept: Base + Overlay

```
k8s/
├── base/                    # Shared foundation
│   ├── kustomization.yaml   # Lists all resources
│   ├── redis.yaml
│   ├── store-api.yaml
│   ├── order-service.yaml
│   └── inventory-worker.yaml
├── overlays/
│   ├── dev/                 # Your local Kind cluster
│   │   ├── kustomization.yaml
│   │   └── patches/
│   │       └── replicas.yaml
│   └── prod/                # Hypothetical production
│       ├── kustomization.yaml
│       └── patches/
│           ├── replicas.yaml
│           └── resources.yaml
```

### Exercises

#### 2.1 — Create a Base
**Goal:** Organize all your Phase 1 YAML files under `k8s/base/`

```yaml
# k8s/base/kustomization.yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: order-system
resources:
  - redis.yaml
  - sqlserver.yaml
  - postgres.yaml
  - kafka.yaml
  - store-api.yaml
  - order-service.yaml
  - inventory-worker.yaml
commonLabels:
  app.kubernetes.io/part-of: order-system
```

```bash
# Preview what Kustomize generates (it merges everything)
kubectl kustomize k8s/base/

# Apply it
kubectl apply -k k8s/base/
```

#### 2.2 — Create a Dev Overlay
**Goal:** Override specific values for your local environment

```yaml
# k8s/overlays/dev/kustomization.yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
resources:
  - ../../base
namePrefix: dev-         # All resources get "dev-" prefix
patches:
  - path: patches/replicas.yaml
```

```yaml
# k8s/overlays/dev/patches/replicas.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: order-service
spec:
  replicas: 1            # Dev = 1 replica (save resources)
```

```bash
# Compare base vs overlay output
kubectl kustomize k8s/base/ > /tmp/base.yaml
kubectl kustomize k8s/overlays/dev/ > /tmp/dev.yaml
diff /tmp/base.yaml /tmp/dev.yaml
```

#### 2.3 — Create a "Prod" Overlay
**Goal:** See how the same base produces a different configuration

- Higher replicas (order-service: 3, store-api: 2)
- Higher resource limits
- Different namespace (`order-system-prod`)
- Different image tags (`:v1.2.3` instead of `:latest`)

**Case Study Connection:** Imagine your order system goes live. Dev runs on Kind with 1
replica and 128Mi RAM. Prod runs on AKS/EKS with 3 replicas and 1Gi RAM. Same base YAML,
different overlays.

### Key Concepts Before Moving On
- [ ] What does `kustomization.yaml` do?
- [ ] How do patches work (strategic merge patch vs JSON patch)?
- [ ] When would you use `namePrefix`/`nameSuffix`?
- [ ] How does `commonLabels` help?
- [ ] Why is Kustomize preferred over Helm for simple cases?

---

## PHASE 3 — Git + GitOps Principles (Week 4)

### What You're Learning
The **philosophy** before the tooling. GitOps = "Git is the single source of truth for
your infrastructure."

### GitOps Core Principles

```
┌──────────────────────────────────────────────────────────┐
│                    GITOPS PRINCIPLES                      │
│                                                           │
│  1. DECLARATIVE    — Entire system described in YAML/code │
│  2. VERSIONED      — Git stores all desired state         │
│  3. AUTOMATED      — Changes auto-applied by agent        │
│  4. SELF-HEALING   — Drift detected & corrected           │
│                                                           │
│  Traditional:  Developer → kubectl apply → Cluster        │
│  GitOps:       Developer → git push → ArgoCD → Cluster   │
└──────────────────────────────────────────────────────────┘
```

### Exercises

#### 3.1 — Set Up Your Git Repository
**Goal:** Structure your repo for GitOps

```
order-processing-system/          # Git root
├── src/
│   ├── StoreApi/                 # Application code
│   ├── OrderService/
│   └── InventoryWorker/
├── k8s/                          # ← This IS your GitOps config
│   ├── base/
│   └── overlays/
│       ├── dev/
│       └── prod/
└── infra/
    └── docker-compose.yml        # Legacy (still useful for local dev)
```

```bash
cd "C:\Users\INT102231\...\Practice\code"
git init
git add .
git commit -m "Initial commit: order processing system"
# Push to GitHub (ArgoCD will watch this repo)
```

#### 3.2 — Experience the "Git Diff = Infra Diff" Concept
**Goal:** Understand that every infrastructure change is a git commit

```bash
# Change order-service replicas in k8s/overlays/dev/
git diff                          # See exactly what changed
git commit -am "Scale order-service to 2 replicas"
git log --oneline                 # Full audit trail of every change

# Compare to the old way:
# kubectl scale deployment order-service --replicas=2
# ↑ No record, no review, no rollback
```

**Case Study Connection:** Your team lead asks "who changed the replica count last Tuesday?"
With GitOps: `git log --since="last Tuesday" -- k8s/`. Without GitOps: ¯\_(ツ)_/¯

#### 3.3 — Understand the Two-Repo Pattern (Important for ArgoCD)

```
OPTION A: Mono-repo (start here)     OPTION B: Two repos (production)
┌─────────────────────┐              ┌──────────────┐  ┌──────────────┐
│ order-system/       │              │ app-code/    │  │ k8s-config/  │
│  ├── src/           │              │  ├── src/    │  │  ├── base/   │
│  ├── k8s/           │              │  └── CI →    │──│  └── overlays│
│  └── Dockerfiles    │              │    builds    │  │              │
└─────────────────────┘              └──────────────┘  └──────────────┘
                                      Code changes      Config changes
                                      trigger CI        trigger ArgoCD
```

Start with **mono-repo** for learning. Understand why the two-repo pattern exists
(separation of concerns, different access controls, CI triggers ≠ CD triggers).

---

## PHASE 4 — ArgoCD (Week 5-6)

### What You're Learning
The **GitOps controller** that watches your Git repo and syncs it to K8s automatically.

### How ArgoCD Fits Your System

```
┌─────────┐   git push    ┌─────────┐   sync    ┌──────────────┐
│ You/IDE  │──────────────►│  GitHub  │◄─────────│   ArgoCD     │
│          │               │  Repo    │  (polls)  │  (in K8s)    │
└─────────┘               └─────────┘           └──────┬───────┘
                                                        │ applies
                                                        ▼
                                                ┌──────────────┐
                                                │ K8s Cluster  │
                                                │ order-system │
                                                │  namespace   │
                                                └──────────────┘
```

### Exercises

#### 4.1 — Install ArgoCD
```bash
# Install ArgoCD into your Kind cluster
kubectl create namespace argocd
kubectl apply -n argocd -f https://raw.githubusercontent.com/argoproj/argo-cd/stable/manifests/install.yaml

# Wait for pods to be ready
kubectl wait --for=condition=ready pod -l app.kubernetes.io/name=argocd-server -n argocd --timeout=120s

# Get the initial admin password
kubectl -n argocd get secret argocd-initial-admin-secret -o jsonpath="{.data.password}" | base64 -d

# Access the UI
kubectl port-forward svc/argocd-server -n argocd 8443:443
# Open https://localhost:8443 — login with admin / <password above>
```

#### 4.2 — Install ArgoCD CLI
```bash
# Windows (via curl or scoop/choco)
# Download from: https://github.com/argoproj/argo-cd/releases/latest

argocd login localhost:8443 --insecure
argocd account update-password    # Change from auto-generated
```

#### 4.3 — Create Your First ArgoCD Application
**Goal:** Point ArgoCD at your Git repo's `k8s/overlays/dev/` directory

```yaml
# k8s/argocd/application.yaml
apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
  name: order-system-dev
  namespace: argocd
spec:
  project: default
  source:
    repoURL: https://github.com/<you>/order-processing-system.git
    targetRevision: main
    path: k8s/overlays/dev        # ← ArgoCD reads Kustomize here
  destination:
    server: https://kubernetes.default.svc
    namespace: order-system
  syncPolicy:
    automated:                     # ← Auto-sync on git push
      prune: true                  # Delete resources removed from git
      selfHeal: true               # Fix manual kubectl changes
    syncOptions:
      - CreateNamespace=true
```

```bash
kubectl apply -f k8s/argocd/application.yaml
# OR
argocd app create order-system-dev \
  --repo https://github.com/<you>/order-processing-system.git \
  --path k8s/overlays/dev \
  --dest-server https://kubernetes.default.svc \
  --dest-namespace order-system \
  --sync-policy automated
```

#### 4.4 — Experience the GitOps Flow
**Goal:** Change something in Git and watch ArgoCD sync it

```bash
# Step 1: Edit replicas in k8s/overlays/dev/
#         Change order-service replicas from 1 → 2

# Step 2: Commit and push
git add -A && git commit -m "Scale order-service to 2 replicas" && git push

# Step 3: Watch ArgoCD UI — it detects "OutOfSync" → auto-syncs → "Synced"

# Step 4: Verify
kubectl get pods -n order-system -l app=order-service
# You should see 2 pods now
```

#### 4.5 — Test Self-Healing
**Goal:** Manually break something and watch ArgoCD fix it

```bash
# Manually delete a pod (simulating someone bypassing GitOps)
kubectl delete pod -l app=store-api -n order-system

# K8s Deployment recreates the pod (this is K8s, not ArgoCD)

# Now do something ArgoCD catches:
kubectl scale deployment order-service --replicas=5 -n order-system
# ArgoCD detects drift → reverts to whatever is in Git (2 replicas)
# Check ArgoCD UI — you'll see it detected and corrected the drift
```

#### 4.6 — Rollback with Git
**Goal:** Understand that rollback = `git revert`

```bash
# Oops, the scaling broke something
git revert HEAD
git push
# ArgoCD syncs the revert → back to 1 replica
# Full audit trail in git log
```

### Key Concepts Before Moving On
- [ ] What is an ArgoCD Application?
- [ ] What's the difference between manual sync and auto-sync?
- [ ] What does `selfHeal: true` do?
- [ ] What does `prune: true` do?
- [ ] How does ArgoCD know about Kustomize? (it detects kustomization.yaml)
- [ ] What is "OutOfSync" vs "Synced" vs "Degraded"?

---

## PHASE 5 — Linkerd Service Mesh (Week 7-8)

### What You're Learning
Transparent **mTLS, observability, and traffic control** between your services — without
changing application code.

### Why Linkerd Matters for Your System

```
WITHOUT LINKERD:                         WITH LINKERD:
OrderService ──HTTP──► StoreApi          OrderService ──mTLS──► StoreApi
     │                                        │
     │ (plain text, no metrics,               │ (encrypted, latency/success
     │  no retries, no visibility)            │  metrics, auto-retries,
     │                                        │  per-route dashboards)
     ▼                                        ▼
"Did the request fail? Check logs..."    "Linkerd dashboard shows 99.2%
                                          success rate, p99 = 45ms"
```

Your system has 3 inter-service communication paths:
1. **OrderService → StoreApi** (HTTP — get product details)
2. **OrderService → Kafka** (TCP — produce messages)
3. **InventoryWorker → Kafka** (TCP — consume messages)

Linkerd secures and observes path #1 beautifully. Paths #2-3 get mTLS for free.

### Exercises

#### 5.1 — Install Linkerd
```bash
# Install the CLI
# Windows: download from https://github.com/linkerd/linkerd2/releases

# Pre-flight check — verifies your cluster is compatible
linkerd check --pre

# Install Linkerd CRDs and control plane
linkerd install --crds | kubectl apply -f -
linkerd install | kubectl apply -f -

# Verify installation
linkerd check
```

#### 5.2 — Inject Linkerd into Your Order System
**Goal:** Add the Linkerd sidecar proxy to all your pods

```bash
# Method 1: Annotate the namespace (all new pods get injected)
kubectl annotate namespace order-system linkerd.io/inject=enabled

# Restart deployments to trigger injection
kubectl rollout restart deployment -n order-system

# Verify sidecars are injected (each pod should have 2 containers)
kubectl get pods -n order-system
# NAME                              READY   STATUS
# order-service-xxx                 2/2     Running    ← 2/2 = sidecar injected!
# store-api-xxx                     2/2     Running
# inventory-worker-xxx              2/2     Running
```

**GitOps way (better):** Add the annotation in your Kustomize overlay:
```yaml
# k8s/overlays/dev/patches/linkerd.yaml
apiVersion: v1
kind: Namespace
metadata:
  name: order-system
  annotations:
    linkerd.io/inject: enabled
```

#### 5.3 — Explore the Dashboard
```bash
# Install the viz extension (metrics + dashboard)
linkerd viz install | kubectl apply -f -
linkerd viz check

# Open the dashboard
linkerd viz dashboard
```

**Now generate traffic with your Postman collection and observe:**
- Success rate of OrderService → StoreApi calls
- Latency percentiles (p50, p95, p99)
- Requests per second
- TCP connections (to Kafka, Redis, databases)

#### 5.4 — Create a Service Profile (Traffic Policy)
**Goal:** Define per-route metrics and retries for the StoreApi

```yaml
# k8s/linkerd/service-profiles/store-api-profile.yaml
apiVersion: linkerd.io/v1alpha2
kind: ServiceProfile
metadata:
  name: store-api.order-system.svc.cluster.local
  namespace: order-system
spec:
  routes:
    - name: GET /api/products
      condition:
        method: GET
        pathRegex: /api/products
      isRetryable: true           # Safe to retry GETs
    - name: GET /api/products/{id}
      condition:
        method: GET
        pathRegex: /api/products/[^/]+
      isRetryable: true
```

Now the Linkerd dashboard shows **per-route metrics**: "GET /api/products has 99.5% success
rate at p99=23ms" vs "GET /api/products/{id} has 97% success rate at p99=150ms".

#### 5.5 — Observe the Order Flow End-to-End
**Goal:** Use Linkerd to trace a full order through your system

```bash
# Watch live traffic
linkerd viz top deployment/order-service -n order-system
# Shows: which services order-service is calling, success rates, latency

linkerd viz tap deployment/order-service -n order-system
# Shows individual requests in real-time:
# req id=0:1 proxy=in  src=10.0.0.5:52341 dst=10.0.0.8:5200 :method=POST :path=/api/orders
# rsp id=0:1 proxy=in  src=10.0.0.5:52341 dst=10.0.0.8:5200 :status=201 latency=45ms

# Verify mTLS is active
linkerd viz edges deployment -n order-system
# SRC             DST           SRC_TLS  DST_TLS
# order-service   store-api     true     true     ← mTLS!
```

### Key Concepts Before Moving On
- [ ] What is a service mesh? Why not just use HTTP?
- [ ] What is a sidecar proxy? How does injection work?
- [ ] What is mTLS and why does it matter?
- [ ] What metrics does Linkerd provide out of the box?
- [ ] What is a ServiceProfile?
- [ ] How does Linkerd differ from Istio? (simpler, lighter, less config)

---

## PHASE 6 — Putting It All Together (Week 9-10)

### The Complete GitOps Pipeline

```
┌─────────────────────────────────────────────────────────────────────┐
│                     FULL GITOPS WORKFLOW                            │
│                                                                     │
│  ┌──────┐  push   ┌────────┐  trigger  ┌─────────┐  push tag      │
│  │ You  │────────►│ GitHub │──────────►│  CI/CD  │───────────┐    │
│  │ edit │  code   │  Repo  │           │(Actions)│           │    │
│  │ code │         └────────┘           └─────────┘           │    │
│  └──────┘              ▲                                      │    │
│                        │ update image tag                     │    │
│                        │ in k8s/overlays/dev/                 │    │
│                        │                                      │    │
│                   ┌────┴────┐                                 │    │
│                   │ CI Bot  │◄────────────────────────────────┘    │
│                   │ commit  │  docker push                         │
│                   └─────────┘  image:v1.2.3                        │
│                        │                                            │
│                        │ ArgoCD detects new commit                  │
│                        ▼                                            │
│                   ┌─────────┐   sync    ┌──────────────────┐       │
│                   │ ArgoCD  │──────────►│   K8s Cluster    │       │
│                   │         │           │  ┌────────────┐  │       │
│                   └─────────┘           │  │ Linkerd    │  │       │
│                        │                │  │ (mTLS +    │  │       │
│                        │ monitors       │  │ metrics)   │  │       │
│                        ▼                │  └────────────┘  │       │
│                   ┌─────────┐           │  ┌────────────┐  │       │
│                   │ Linkerd │           │  │ Your 3     │  │       │
│                   │  Viz    │           │  │ Services   │  │       │
│                   │Dashboard│           │  └────────────┘  │       │
│                   └─────────┘           └──────────────────┘       │
└─────────────────────────────────────────────────────────────────────┘
```

### Exercise 6.1 — Simulate a Real Feature Deployment

**Scenario:** Add a `/health` endpoint to OrderService and deploy via GitOps.

1. **Code change:** Add `app.MapGet("/health", () => "OK");` in OrderService's Program.cs
2. **Build + tag:** `docker build -t order-system/order-service:v1.1.0 .`
3. **Load into Kind:** `kind load docker-image order-system/order-service:v1.1.0`
4. **Update K8s config:** Change image tag in `k8s/overlays/dev/` Kustomize patch
5. **Commit + push:** Both the code change and the K8s config change
6. **Watch ArgoCD:** It detects the change, syncs, rolls out the new version
7. **Watch Linkerd:** See the rolling update — old pods drain, new pods start
8. **Verify:** `kubectl port-forward svc/order-service 5200:5200 -n order-system`
   then `curl http://localhost:5200/health`

### Exercise 6.2 — Break Something and Recover

**Scenario:** Deploy a bad image and roll back via Git.

1. Push a broken image (e.g., wrong connection string)
2. ArgoCD deploys it → pods crash → `Degraded` status in ArgoCD UI
3. Linkerd shows success rate dropping
4. Fix: `git revert HEAD && git push`
5. ArgoCD syncs the revert → healthy again

---

## Quick Reference: Tool Installation for Windows

```powershell
# Kind (you already have this)
# kubectl (you already have this)

# Kustomize (built into kubectl — no separate install needed)
kubectl kustomize --help

# Linkerd CLI
# Download from: https://github.com/linkerd/linkerd2/releases
# Add to PATH, then: linkerd version

# ArgoCD CLI
# Download from: https://github.com/argoproj/argo-cd/releases
# Or: choco install argocd-cli / scoop install argocd

# Verify all tools
kubectl version --client
kubectl kustomize --help
linkerd version
argocd version --client
```

---

## Suggested Learning Resources

| Topic      | Resource                                                  | Type     |
|------------|-----------------------------------------------------------|----------|
| K8s basics | https://kubernetes.io/docs/tutorials/                     | Official |
| Kustomize  | https://kubectl.docs.kubernetes.io/guides/introduction/   | Official |
| ArgoCD     | https://argo-cd.readthedocs.io/en/stable/getting_started/ | Official |
| Linkerd    | https://linkerd.io/2/getting-started/                     | Official |
| GitOps     | "GitOps and Kubernetes" by Billy Yuen (Manning)           | Book     |
| Video      | TechWorld with Nana — ArgoCD Tutorial                     | YouTube  |

---

## Timeline Summary

| Week  | Phase                     | Milestone                                       |
|-------|---------------------------|-------------------------------------------------|
| 1-2   | K8s Fundamentals          | All 3 services + infra running in Kind           |
| 3     | Kustomize                 | Base + dev overlay, `kubectl apply -k` working  |
| 4     | Git + GitOps Principles   | Repo structured, understand declarative config  |
| 5-6   | ArgoCD                    | Auto-sync from Git → K8s, self-healing working  |
| 7-8   | Linkerd                   | mTLS, dashboard, per-route metrics visible      |
| 9-10  | Integration               | Full pipeline: code → git → ArgoCD → K8s + mesh |
