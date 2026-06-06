# KT-10: ArgoCD — GitOps Controller Deep Dive
**Author:** Copilot KT Session  
**Created:** 2026-06-05  
**Audience:** Developers implementing GitOps with ArgoCD on Kubernetes  
**Status:** Living Document

---

## Table of Contents
1. [What ArgoCD Is](#1-what-argocd-is)
2. [Architecture — Components](#2-architecture--components)
3. [Core Concept: The Application CRD](#3-core-concept-the-application-crd)
4. [Sync Lifecycle — States & Transitions](#4-sync-lifecycle--states--transitions)
5. [Sync Policies — Manual vs Automated](#5-sync-policies--manual-vs-automated)
6. [Sync Waves & Hooks](#6-sync-waves--hooks)
7. [Health Assessment](#7-health-assessment)
8. [Projects — Multi-Tenancy & RBAC](#8-projects--multi-tenancy--rbac)
9. [ApplicationSets — Dynamic App Generation](#9-applicationsets--dynamic-app-generation)
10. [Secrets & Credentials](#10-secrets--credentials)
11. [Notifications & Alerts](#11-notifications--alerts)
12. [Disaster Recovery & Backup](#12-disaster-recovery--backup)
13. [Multi-Cluster Management](#13-multi-cluster-management)
14. [ArgoCD with Kustomize, Helm, and Plain YAML](#14-argocd-with-kustomize-helm-and-plain-yaml)
15. [Operational Patterns & Best Practices](#15-operational-patterns--best-practices)
16. [CLI & UI Reference](#16-cli--ui-reference)
17. [Troubleshooting Guide](#17-troubleshooting-guide)

---

## 1. What ArgoCD Is

ArgoCD is a **declarative, GitOps continuous delivery tool** for Kubernetes.

```
┌─────────────────────────────────────────────────────────────┐
│  In one sentence:                                            │
│                                                              │
│  ArgoCD watches a Git repo and makes your Kubernetes         │
│  cluster match whatever YAML is in that repo — continuously. │
└─────────────────────────────────────────────────────────────┘
```

### What ArgoCD Does NOT Do

| Not This | Use This Instead |
|----------|-------------------|
| Build Docker images | GitHub Actions, Jenkins, GitLab CI |
| Run tests | CI pipeline |
| Manage secrets natively | Sealed Secrets, Vault, SOPS |
| Replace Helm/Kustomize | It **uses** them as config tools |
| Work outside Kubernetes | It's K8s-native only |

ArgoCD is **purely the CD** (Continuous Delivery) part of your pipeline.

---

## 2. Architecture — Components

```
┌─────────────────────────────────────────────────────────────────┐
│                    ArgoCD ARCHITECTURE                           │
│                    (runs inside K8s)                             │
│                                                                  │
│  ┌────────────────┐    ┌──────────────────────────────────────┐ │
│  │   argocd-server │    │  argocd-repo-server                  │ │
│  │                 │    │                                       │ │
│  │  • API server   │    │  • Clones Git repos                  │ │
│  │  • Web UI       │    │  • Runs kustomize build / helm       │ │
│  │  • gRPC/REST    │    │    template to generate manifests    │ │
│  │  • Auth (OIDC)  │    │  • Caches rendered manifests         │ │
│  │                 │    │  • Stateless — can scale horizontally│ │
│  └────────┬───────┘    └──────────────┬───────────────────────┘ │
│           │                            │                         │
│           │         ┌──────────────────┴────────┐               │
│           │         │  argocd-application-       │               │
│           │         │  controller                │               │
│           │         │                             │               │
│           │         │  • The brain of ArgoCD      │               │
│           │         │  • Watches Application CRDs │               │
│           │         │  • Compares desired vs live  │               │
│           │         │  • Triggers sync operations  │               │
│           │         │  • Runs health checks        │               │
│           │         └──────────────┬──────────────┘               │
│           │                        │                              │
│           │    ┌───────────────────┴──────────┐                  │
│           │    │  Redis (optional cache)       │                  │
│           │    │  • Caches app state            │                  │
│           │    │  • Not for application data    │                  │
│           │    └──────────────────────────────┘                  │
│           │                                                      │
│  ┌────────┴──────────────────────────────────────┐              │
│  │  argocd-dex-server                             │              │
│  │  • SSO/OIDC authentication                     │              │
│  │  • Integrates with GitHub, LDAP, SAML, etc.    │              │
│  └────────────────────────────────────────────────┘              │
└─────────────────────────────────────────────────────────────────┘
```

### Component Summary

| Component | Replicas | Purpose | Scaling |
|-----------|----------|---------|---------|
| `argocd-server` | 1-3 | API + Web UI | HA with multiple replicas |
| `argocd-repo-server` | 1-3 | Git clone + manifest rendering | Scale for many repos |
| `argocd-application-controller` | 1 | Reconciliation engine | Single leader (sharding for scale) |
| `argocd-dex-server` | 1 | SSO authentication | Optional |
| `argocd-redis` | 1 | Caching | Optional (embedded available) |
| `argocd-notifications-controller` | 1 | Slack/email alerts | Optional |

---

## 3. Core Concept: The Application CRD

An ArgoCD **Application** is a Custom Resource (CRD) that defines:
- **WHERE** to get config (Git repo + path)
- **WHERE** to deploy (K8s cluster + namespace)
- **HOW** to sync (manual/auto, prune, self-heal)

```yaml
apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
  name: order-system-dev                    # Human-readable name
  namespace: argocd                          # Must be in argocd namespace
  finalizers:
    - resources-finalizer.argocd.argoproj.io  # Clean up on delete
spec:
  # ── PROJECT ────────────────────────────
  project: default                           # RBAC boundary (see Section 8)

  # ── SOURCE (where to read config) ─────
  source:
    repoURL: https://github.com/you/order-system.git
    targetRevision: main                     # Branch, tag, or commit SHA
    path: k8s/overlays/dev                   # Directory in repo

  # ── DESTINATION (where to deploy) ──────
  destination:
    server: https://kubernetes.default.svc   # This cluster (in-cluster)
    namespace: order-system                   # Target namespace

  # ── SYNC POLICY ────────────────────────
  syncPolicy:
    automated:                               # Enable auto-sync
      prune: true                            # Delete removed resources
      selfHeal: true                         # Revert manual changes
    syncOptions:
      - CreateNamespace=true                 # Create ns if missing
      - PrunePropagationPolicy=foreground    # Wait for dependents
      - PruneLast=true                       # Prune after sync
    retry:
      limit: 5                               # Retry on failure
      backoff:
        duration: 5s
        factor: 2
        maxDuration: 3m

  # ── IGNORE DIFFERENCES ────────────────
  ignoreDifferences:
    - group: apps
      kind: Deployment
      jsonPointers:
        - /spec/replicas                     # Let HPA manage replicas
```

### Application Fields Explained

```
source:
  repoURL      → Which Git repo?
  targetRevision → Which branch/tag/commit?
  path         → Which directory in the repo?
                  ArgoCD auto-detects if this contains:
                  - kustomization.yaml → runs kustomize build
                  - Chart.yaml → runs helm template
                  - plain .yaml files → applies directly

destination:
  server       → Which K8s cluster? (URL or name)
  namespace    → Which namespace?

syncPolicy:
  automated    → Should ArgoCD auto-apply changes?
  prune        → Should it delete resources removed from Git?
  selfHeal     → Should it revert manual kubectl changes?
```

---

## 4. Sync Lifecycle — States & Transitions

```
┌─────────────────────────────────────────────────────────────────┐
│                  APPLICATION STATES                               │
│                                                                   │
│   ┌──────────┐     git push      ┌──────────────┐               │
│   │  Synced   │────────────────►│  OutOfSync    │               │
│   │  (green)  │                  │  (yellow)     │               │
│   └──────────┘◄───────────────  └──────┬───────┘               │
│        ▲          sync complete         │                        │
│        │                                │ sync triggered         │
│        │                                ▼                        │
│        │                         ┌──────────────┐               │
│        │                         │  Syncing     │               │
│        │                         │  (blue)      │               │
│        │                         └──────┬───────┘               │
│        │                                │                        │
│        │                    ┌───────────┼───────────┐           │
│        │                    ▼                       ▼           │
│   ┌──────────┐       ┌──────────┐           ┌──────────┐      │
│   │  Healthy  │       │ Degraded │           │  Failed  │      │
│   │  (green)  │       │ (orange) │           │  (red)   │      │
│   └──────────┘       └──────────┘           └──────────┘      │
│   Pods running,       Some pods             Apply failed,      │
│   probes passing      failing               YAML errors        │
│                                                                   │
└─────────────────────────────────────────────────────────────────┘
```

### Sync Status vs Health Status

ArgoCD tracks **two independent statuses**:

| Dimension | Values | Meaning |
|-----------|--------|---------|
| **Sync Status** | Synced, OutOfSync, Unknown | Does Git match cluster? |
| **Health Status** | Healthy, Progressing, Degraded, Suspended, Missing, Unknown | Are pods running? |

**Both matter:**
- Synced + Healthy = Everything is good ✅
- Synced + Degraded = Config is applied but pods are crashing ⚠️
- OutOfSync + Healthy = Someone changed the cluster manually 🔄
- OutOfSync + Degraded = Config change needed + something is broken 🔴

---

## 5. Sync Policies — Manual vs Automated

### Manual Sync (Safe, For Production)

```yaml
syncPolicy: {}       # Empty = manual sync
```

- ArgoCD detects changes → marks "OutOfSync"
- Human clicks "Sync" in UI or runs `argocd app sync order-system-prod`
- Good for production — change goes through approval

### Automated Sync (Fast, For Dev/Staging)

```yaml
syncPolicy:
  automated:
    prune: true
    selfHeal: true
```

- ArgoCD detects changes → automatically applies
- No human intervention needed
- Good for dev environments — fast feedback loop

### The prune & selfHeal Matrix

| prune | selfHeal | Behavior |
|-------|----------|----------|
| `false` | `false` | Auto-apply new/changed resources, ignore deletions and drift |
| `true` | `false` | Auto-apply + auto-delete, but don't fix manual changes |
| `false` | `true` | Auto-apply + fix drift, but don't delete resources removed from Git |
| `true` | `true` | Full GitOps: auto-apply, auto-delete, auto-fix drift |

**Recommendation:** `prune: true` + `selfHeal: true` for dev. Manual sync for prod.

---

## 6. Sync Waves & Hooks

### Problem: Deployment Order

Your system needs infra before apps:
```
PostgreSQL must be running BEFORE OrderService starts
SQL Server must be running BEFORE StoreApi starts
Kafka must be running BEFORE InventoryWorker starts
```

### Sync Waves

```yaml
# Wave 0 — deploys first
apiVersion: apps/v1
kind: Deployment
metadata:
  name: postgres
  annotations:
    argocd.argoproj.io/sync-wave: "0"      # ← Deploy first

---
# Wave 1 — deploys after wave 0 is healthy
apiVersion: apps/v1
kind: Deployment
metadata:
  name: kafka
  annotations:
    argocd.argoproj.io/sync-wave: "1"      # ← Deploy second

---
# Wave 2 — deploys after wave 1 is healthy
apiVersion: apps/v1
kind: Deployment
metadata:
  name: order-service
  annotations:
    argocd.argoproj.io/sync-wave: "2"      # ← Deploy third
```

```
Sync execution order:

  Wave 0: namespace, secrets, configmaps, databases (postgres, sqlserver, redis)
           ↓ wait until healthy
  Wave 1: kafka, zookeeper
           ↓ wait until healthy
  Wave 2: store-api, order-service, inventory-worker
```

### Sync Hooks

Run **Jobs** at specific points in the sync lifecycle:

```yaml
apiVersion: batch/v1
kind: Job
metadata:
  name: db-migrate
  annotations:
    argocd.argoproj.io/hook: PreSync       # Run BEFORE sync
    argocd.argoproj.io/hook-delete-policy: HookSucceeded
spec:
  template:
    spec:
      containers:
        - name: migrate
          image: order-system/db-migrate:v1
          command: ["dotnet", "ef", "database", "update"]
      restartPolicy: Never
```

| Hook | When | Use Case |
|------|------|----------|
| `PreSync` | Before sync starts | DB migrations, config validation |
| `Sync` | During sync (with resources) | Deploy alongside resources |
| `PostSync` | After all resources healthy | Smoke tests, notifications |
| `SyncFail` | When sync fails | Cleanup, alerting |
| `Skip` | Never (resource managed externally) | Operator-managed resources |

---

## 7. Health Assessment

ArgoCD has built-in health checks for common K8s resources:

| Resource | Healthy When |
|----------|-------------|
| Deployment | All replicas ready, no rollout in progress |
| StatefulSet | All replicas ready |
| DaemonSet | Desired = current = ready |
| Service | Always healthy (static resource) |
| Ingress | Always healthy |
| PVC | Bound |
| Pod | Running + containers ready |
| Job | Succeeded |

### Custom Health Checks (Lua Scripts)

```yaml
# argocd-cm ConfigMap
data:
  resource.customizations.health.kafka.strimzi.io_Kafka: |
    hs = {}
    if obj.status ~= nil and obj.status.conditions ~= nil then
      for _, condition in ipairs(obj.status.conditions) do
        if condition.type == "Ready" and condition.status == "True" then
          hs.status = "Healthy"
          hs.message = "Kafka cluster is ready"
          return hs
        end
      end
    end
    hs.status = "Progressing"
    hs.message = "Waiting for Kafka cluster"
    return hs
```

---

## 8. Projects — Multi-Tenancy & RBAC

### What Is an AppProject?

Projects define **boundaries** — which repos, clusters, namespaces, and resources
an Application is allowed to use.

```yaml
apiVersion: argoproj.io/v1alpha1
kind: AppProject
metadata:
  name: order-system
  namespace: argocd
spec:
  description: Order processing microservices

  # Which repos can be used as sources
  sourceRepos:
    - https://github.com/you/order-system.git
    - https://github.com/you/k8s-config.git

  # Which clusters and namespaces can be targeted
  destinations:
    - server: https://kubernetes.default.svc
      namespace: order-system
    - server: https://kubernetes.default.svc
      namespace: order-system-staging

  # Which K8s resource types are allowed
  clusterResourceWhitelist:
    - group: ''
      kind: Namespace
  namespaceResourceWhitelist:
    - group: '*'
      kind: '*'

  # RBAC roles within the project
  roles:
    - name: developer
      description: Can sync dev apps
      policies:
        - p, proj:order-system:developer, applications, sync, order-system/*, allow
        - p, proj:order-system:developer, applications, get, order-system/*, allow
      groups:
        - my-github-org:backend-team
```

### Why Projects Matter

```
Without Projects:                     With Projects:
─────────────────                     ───────────────
Any app can deploy to any namespace   Apps restricted to allowed namespaces
Any repo can be used                  Only approved repos allowed
Any team can do anything              RBAC per team
```

---

## 9. ApplicationSets — Dynamic App Generation

### Problem: Managing Many Similar Applications

```
You have 3 services × 3 environments = 9 ArgoCD Applications.
Writing 9 Application YAMLs is tedious and error-prone.
```

### Solution: ApplicationSet Generators

```yaml
apiVersion: argoproj.io/v1alpha1
kind: ApplicationSet
metadata:
  name: order-system
  namespace: argocd
spec:
  generators:
    - matrix:
        generators:
          # Generator 1: environments
          - list:
              elements:
                - env: dev
                  namespace: order-system-dev
                - env: staging
                  namespace: order-system-staging
                - env: prod
                  namespace: order-system-prod
          # Generator 2: services
          - list:
              elements:
                - service: store-api
                - service: order-service
                - service: inventory-worker
  template:
    metadata:
      name: '{{service}}-{{env}}'            # e.g., "store-api-dev"
    spec:
      project: order-system
      source:
        repoURL: https://github.com/you/order-system.git
        targetRevision: main
        path: 'k8s/{{service}}/overlays/{{env}}'
      destination:
        server: https://kubernetes.default.svc
        namespace: '{{namespace}}'
      syncPolicy:
        automated:
          prune: true
```

This generates **9 Applications** from one template:
`store-api-dev`, `store-api-staging`, `store-api-prod`,
`order-service-dev`, `order-service-staging`, `order-service-prod`, etc.

### Generator Types

| Generator | Creates apps from | Use case |
|-----------|------------------|----------|
| `list` | Static list of values | Fixed set of environments |
| `git` (directories) | Directories in a Git repo | One app per directory |
| `git` (files) | JSON/YAML files in Git | Config-driven app generation |
| `cluster` | Registered ArgoCD clusters | Deploy to all clusters |
| `matrix` | Cartesian product of 2 generators | Services × environments |
| `merge` | Merge multiple generators | Complex combinations |
| `pullRequest` | Open PRs in a repo | Preview environments |

---

## 10. Secrets & Credentials

### Repository Credentials

```bash
# HTTPS with token
argocd repo add https://github.com/you/private-repo.git \
  --username git \
  --password ghp_xxxxxxxxxxxx

# SSH
argocd repo add git@github.com:you/private-repo.git \
  --ssh-private-key-path ~/.ssh/id_rsa

# Stored as K8s Secrets in argocd namespace
kubectl get secrets -n argocd -l argocd.argoproj.io/secret-type=repository
```

### Cluster Credentials (for Multi-Cluster)

```bash
# Add an external cluster
argocd cluster add my-production-context

# ArgoCD creates a ServiceAccount in the target cluster
# and stores the token as a Secret
```

---

## 11. Notifications & Alerts

```yaml
# argocd-notifications-cm ConfigMap
apiVersion: v1
kind: ConfigMap
metadata:
  name: argocd-notifications-cm
  namespace: argocd
data:
  # Slack integration
  service.slack: |
    token: $slack-token
  
  # Templates
  template.app-sync-succeeded: |
    message: |
      ✅ {{.app.metadata.name}} synced successfully
      Revision: {{.app.status.sync.revision}}
  
  template.app-sync-failed: |
    message: |
      ❌ {{.app.metadata.name}} sync failed!
      {{range .app.status.conditions}}
        {{.message}}
      {{end}}
  
  # Triggers
  trigger.on-sync-succeeded: |
    - when: app.status.sync.status == 'Synced'
      send: [app-sync-succeeded]
  
  trigger.on-sync-failed: |
    - when: app.status.sync.status == 'Unknown' && app.status.health.status == 'Degraded'
      send: [app-sync-failed]
```

```yaml
# On the Application — subscribe to notifications
metadata:
  annotations:
    notifications.argoproj.io/subscribe.on-sync-succeeded.slack: backend-deploys
    notifications.argoproj.io/subscribe.on-sync-failed.slack: backend-alerts
```

---

## 12. Disaster Recovery & Backup

### What to Back Up

```
ArgoCD's state is stored as K8s resources:
  1. Application CRDs           → kubectl get app -n argocd -o yaml
  2. AppProject CRDs            → kubectl get appproject -n argocd -o yaml
  3. Repository Secrets          → kubectl get secret -n argocd -l argocd.argoproj.io/secret-type
  4. Cluster Secrets             → kubectl get secret -n argocd -l argocd.argoproj.io/secret-type
  5. ConfigMaps (argocd-cm, etc) → kubectl get cm -n argocd -o yaml

But the REAL backup is your Git repo!
If ArgoCD dies, reinstall it and re-apply your Application YAMLs.
It will re-sync everything from Git.
```

### Recovery

```bash
# Nuclear option: delete and reinstall ArgoCD
kubectl delete namespace argocd
kubectl create namespace argocd
kubectl apply -n argocd -f https://raw.githubusercontent.com/.../install.yaml

# Re-apply your Applications (these are in Git!)
kubectl apply -f k8s/argocd/applications/

# ArgoCD re-syncs everything from Git — cluster is restored
```

---

## 13. Multi-Cluster Management

```
┌─────────────────────────────────────────────────────┐
│                ArgoCD (Management Cluster)            │
│                                                       │
│   ┌──────────┐  ┌──────────┐  ┌──────────┐          │
│   │ App: dev │  │App: stg  │  │App: prod │          │
│   └────┬─────┘  └────┬─────┘  └────┬─────┘          │
│        │              │              │                │
└────────┼──────────────┼──────────────┼────────────────┘
         │              │              │
         ▼              ▼              ▼
    ┌─────────┐   ┌─────────┐   ┌─────────┐
    │Dev K8s  │   │Stg K8s  │   │Prod K8s │
    │Cluster  │   │Cluster  │   │Cluster  │
    └─────────┘   └─────────┘   └─────────┘

One ArgoCD instance manages multiple clusters.
Each Application's `destination.server` points to a different cluster.
```

---

## 14. ArgoCD with Kustomize, Helm, and Plain YAML

ArgoCD auto-detects the config tool by looking at the source path:

| Files Found | Tool Used | Internal Command |
|-------------|-----------|-----------------|
| `kustomization.yaml` | Kustomize | `kustomize build <path>` |
| `Chart.yaml` | Helm | `helm template <chart>` |
| `*.yaml` (plain) | Directory | Direct apply |
| `jsonnet` files | Jsonnet | `jsonnet <file>` |

### Kustomize Overrides in ArgoCD

```yaml
source:
  path: k8s/overlays/dev
  kustomize:
    namePrefix: dev-
    images:
      - order-system/order-service=ghcr.io/you/order-service:v1.2.3
    commonLabels:
      deploy-tool: argocd
```

### Helm Overrides in ArgoCD

```yaml
source:
  chart: redis
  repoURL: https://charts.bitnami.com/bitnami
  targetRevision: 17.0.0
  helm:
    values: |
      architecture: standalone
      auth:
        enabled: false
      master:
        resources:
          limits:
            memory: 256Mi
```

---

## 15. Operational Patterns & Best Practices

### The "App of Apps" Pattern

One parent Application manages all child Applications:

```yaml
# k8s/argocd/app-of-apps.yaml
apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
  name: root-app
  namespace: argocd
spec:
  source:
    path: k8s/argocd/applications/    # Directory of Application YAMLs
  destination:
    server: https://kubernetes.default.svc
    namespace: argocd
```

```
k8s/argocd/applications/
├── store-api-dev.yaml          # Application CRD
├── order-service-dev.yaml      # Application CRD
├── inventory-worker-dev.yaml   # Application CRD
└── infra-dev.yaml              # Application CRD
```

### Image Updater (CI → Git Bridge)

ArgoCD Image Updater watches container registries and auto-updates image tags in Git:

```yaml
metadata:
  annotations:
    argocd-image-updater.argoproj.io/image-list: |
      store-api=ghcr.io/you/store-api
      order-service=ghcr.io/you/order-service
    argocd-image-updater.argoproj.io/store-api.update-strategy: semver
    argocd-image-updater.argoproj.io/order-service.update-strategy: latest
```

### Progressive Delivery (with Argo Rollouts)

```
Instead of Deployment, use Rollout for canary/blue-green:

  1. Deploy new version to 10% of traffic
  2. Monitor Linkerd metrics (success rate, latency)
  3. If healthy → promote to 100%
  4. If errors → auto-rollback

This connects beautifully with Linkerd (Phase 5 of your roadmap).
```

---

## 16. CLI & UI Reference

### Essential CLI Commands

```bash
# ── APP MANAGEMENT ─────────────────────────
argocd app list                              # List all applications
argocd app get order-system-dev              # Detailed status
argocd app sync order-system-dev             # Manual sync
argocd app diff order-system-dev             # Show what would change
argocd app history order-system-dev          # Deployment history
argocd app rollback order-system-dev 2       # Rollback to revision 2

# ── SYNC OPTIONS ───────────────────────────
argocd app sync order-system-dev --prune     # Sync and prune
argocd app sync order-system-dev --force     # Force sync (recreate)
argocd app sync order-system-dev --dry-run   # Preview only
argocd app sync order-system-dev \
  --resource apps/Deployment/order-service   # Sync specific resource

# ── HEALTH & DEBUGGING ────────────────────
argocd app logs order-system-dev             # Pod logs
argocd app resources order-system-dev        # List managed resources
argocd app manifests order-system-dev        # Show rendered manifests
argocd app actions list order-system-dev     # Available actions

# ── REPO & CLUSTER ────────────────────────
argocd repo list                             # List connected repos
argocd cluster list                          # List connected clusters
argocd proj list                             # List projects
```

### UI Overview

```
┌─────────────────────────────────────────────────────────────┐
│  ArgoCD Dashboard                                            │
│                                                              │
│  ┌─── Applications ───────────────────────────────────────┐ │
│  │                                                         │ │
│  │  ● order-system-dev      Synced   Healthy    main      │ │
│  │  ● order-system-staging  Synced   Healthy    v1.2.3    │ │
│  │  ○ order-system-prod     OutOfSync Healthy   v1.2.2    │ │
│  │                                                         │ │
│  └─────────────────────────────────────────────────────────┘ │
│                                                              │
│  Click an app → Resource tree view:                          │
│                                                              │
│  Application                                                 │
│  ├── Service: store-api           ● Healthy                 │
│  ├── Deployment: store-api        ● Healthy                 │
│  │   └── ReplicaSet: store-api-xxx                          │
│  │       └── Pod: store-api-xxx-yyy  ● Running              │
│  ├── Service: order-service       ● Healthy                 │
│  ├── Deployment: order-service    ● Healthy                 │
│  │   └── ReplicaSet: order-service-xxx                      │
│  │       ├── Pod: order-service-xxx-aaa  ● Running          │
│  │       └── Pod: order-service-xxx-bbb  ● Running          │
│  └── Deployment: inventory-worker ● Healthy                 │
│                                                              │
│  [Sync] [Refresh] [Delete] [History] [Diff] [Logs]         │
└─────────────────────────────────────────────────────────────┘
```

---

## 17. Troubleshooting Guide

| Symptom | Likely Cause | Debug Command |
|---------|-------------|---------------|
| App stuck "Progressing" | Pods not starting | `argocd app get <app> --show-operation` |
| "ComparisonError" | Invalid YAML or Kustomize error | `argocd app manifests <app>` to see render errors |
| "OutOfSync" won't resolve | Ignored fields changing | Add to `ignoreDifferences` |
| Sync fails with "namespace not found" | Missing `CreateNamespace=true` | Add to `syncOptions` |
| "permission denied" on sync | Project restrictions | Check `argocd proj get <project>` |
| App not auto-syncing | Policy missing or webhook not configured | Check `syncPolicy.automated` |
| "already exists" errors | Resource managed by another app | One resource = one ArgoCD app |
| Slow sync (> 5 min) | Large repo or too many resources | Use targeted `path`, split into multiple apps |
