# KT-08: Kustomize — Kubernetes Configuration Management Deep Dive
**Author:** Copilot KT Session  
**Created:** 2026-06-05  
**Audience:** Developers moving from docker-compose to declarative K8s config management  
**Status:** Living Document

---

## Table of Contents
1. [The Problem Kustomize Solves](#1-the-problem-kustomize-solves)
2. [Core Mental Model — Base + Overlay](#2-core-mental-model--base--overlay)
3. [The kustomization.yaml File](#3-the-kustomizationyaml-file)
4. [Patches — The Heart of Kustomize](#4-patches--the-heart-of-kustomize)
5. [ConfigMap & Secret Generators](#5-configmap--secret-generators)
6. [Common Transformers](#6-common-transformers)
7. [Components — Reusable Mixins](#7-components--reusable-mixins)
8. [Kustomize vs Helm](#8-kustomize-vs-helm)
9. [Directory Structure Patterns](#9-directory-structure-patterns)
10. [How ArgoCD Uses Kustomize](#10-how-argocd-uses-kustomize)
11. [Common Mistakes & Gotchas](#11-common-mistakes--gotchas)
12. [Command Reference](#12-command-reference)

---

## 1. The Problem Kustomize Solves

### The Naive Approach (Copy-Paste YAML)

```
k8s/
├── dev/
│   ├── redis.yaml            ← 95% identical to prod/redis.yaml
│   ├── order-service.yaml    ← Only replicas & image tag differ
│   └── store-api.yaml
└── prod/
    ├── redis.yaml            ← Duplicated!
    ├── order-service.yaml    ← Drift between dev & prod over time
    └── store-api.yaml
```

**Problems:**
- **Duplication:** 90% of YAML is identical across environments
- **Drift:** Someone fixes a bug in dev YAML but forgets to copy to prod
- **No templating:** Kubernetes YAML has no `if/else` or `{{ variables }}`

### What Kustomize Does

Kustomize takes a **base** set of YAML files and lets you **overlay** environment-specific
changes on top — without modifying the originals.

```
                  ┌──────────────┐
                  │  base/       │   The "template" — shared config
                  │  (original)  │   Never modified directly by overlays
                  └──────┬───────┘
                         │
              ┌──────────┼──────────┐
              ▼                     ▼
       ┌──────────┐          ┌──────────┐
       │ dev/     │          │ prod/    │
       │ overlay  │          │ overlay  │
       │ (patches)│          │ (patches)│
       └──────────┘          └──────────┘
       replicas: 1           replicas: 3
       image: :latest        image: :v1.2.3
       memory: 128Mi         memory: 1Gi
```

**Key principle:** Kustomize is **template-free**. It works by **merging and patching**
real Kubernetes YAML — no `{{ }}` syntax, no logic operators, no rendering engine.

---

## 2. Core Mental Model — Base + Overlay

### What Is a "Base"?

A base is a directory containing:
1. Valid Kubernetes YAML manifests
2. A `kustomization.yaml` that lists them

```
k8s/base/
├── kustomization.yaml       ← Required — declares what's in this base
├── deployment.yaml           ← Standard K8s Deployment
├── service.yaml              ← Standard K8s Service
└── configmap.yaml            ← Standard K8s ConfigMap
```

The base YAML files are **complete, valid Kubernetes resources**. You could `kubectl apply`
them directly. Kustomize doesn't require any special syntax in your YAML.

### What Is an "Overlay"?

An overlay is a directory that **references a base** and applies modifications:

```
k8s/overlays/dev/
├── kustomization.yaml        ← References ../../base, adds patches
└── patches/
    └── replicas.yaml          ← Only the fields you want to change
```

### The Rendering Pipeline

```
┌────────────────────────────────────────────────────────────────┐
│                    KUSTOMIZE PIPELINE                          │
│                                                                │
│  1. LOAD         Read all resources listed in kustomization    │
│       ↓                                                        │
│  2. MERGE        Combine base resources with overlay patches   │
│       ↓                                                        │
│  3. TRANSFORM    Apply commonLabels, namePrefix, namespace     │
│       ↓                                                        │
│  4. GENERATE     Create ConfigMaps/Secrets from generators     │
│       ↓                                                        │
│  5. OUTPUT       Emit final YAML to stdout                     │
│                                                                │
│  kubectl kustomize k8s/overlays/dev/                           │
│  ──────────────────────────────────────                        │
│  Runs steps 1-5 and prints the result                         │
│                                                                │
│  kubectl apply -k k8s/overlays/dev/                            │
│  ──────────────────────────────────────                        │
│  Runs steps 1-5 and applies to cluster                        │
└────────────────────────────────────────────────────────────────┘
```

---

## 3. The kustomization.yaml File

This is the **only file Kustomize cares about**. Every Kustomize directory must have one.

### Complete Field Reference

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization

# ── RESOURCES ──────────────────────────────────────────
# What K8s manifests to include
resources:
  - deployment.yaml              # Local file
  - service.yaml
  - ../base                      # Another kustomization directory
  - github.com/org/repo/k8s      # Remote Git URL (avoid in prod)

# ── PATCHES ────────────────────────────────────────────
# Modifications to apply on top of resources
patches:
  - path: patches/replicas.yaml          # Strategic Merge Patch (file)
  - target:                               # JSON Patch (inline)
      kind: Deployment
      name: order-service
    patch: |
      - op: replace
        path: /spec/replicas
        value: 3

# ── GENERATORS ─────────────────────────────────────────
# Auto-create ConfigMaps and Secrets
configMapGenerator:
  - name: app-config
    literals:
      - KAFKA_SERVERS=kafka:9092
    files:
      - config.properties

secretGenerator:
  - name: app-secrets
    literals:
      - DB_PASSWORD=ChangeMeNow123!
    type: Opaque

# ── TRANSFORMERS ───────────────────────────────────────
# Global modifications applied to ALL resources
namespace: order-system            # Set namespace on everything
namePrefix: dev-                   # Prefix all resource names
nameSuffix: -v2                    # Suffix all resource names
commonLabels:                      # Add labels to ALL resources + selectors
  env: dev
  app.kubernetes.io/part-of: order-system
commonAnnotations:                 # Add annotations to ALL resources
  team: backend

# ── IMAGES ─────────────────────────────────────────────
# Override image tags without patches
images:
  - name: order-system/store-api
    newTag: v1.2.3
  - name: order-system/order-service
    newName: registry.example.com/order-service
    newTag: v2.0.0

# ── REPLICAS ───────────────────────────────────────────
# Quick replica count override (Kustomize 4.1+)
replicas:
  - name: order-service
    count: 3
```

### Evaluation Order

Kustomize processes fields in this fixed order:
1. `resources` — load YAML files
2. `generators` — create ConfigMaps/Secrets
3. `patches` — apply modifications
4. `transformers` — namespace, namePrefix, labels, annotations
5. `images` — replace image references
6. `replicas` — override replica counts

This order matters! Labels from `commonLabels` are applied **after** patches.

---

## 4. Patches — The Heart of Kustomize

### Two Types of Patches

| Type | When to Use | Syntax |
|------|-------------|--------|
| **Strategic Merge Patch** | Change/add fields in a resource | Partial YAML that merges |
| **JSON Patch (RFC 6902)** | Precise add/remove/replace operations | Array of operations |

### Strategic Merge Patch (Most Common)

You write a **partial** resource with only the fields you want to change.
Kustomize identifies the target by `kind` + `metadata.name` and merges.

```yaml
# patches/increase-memory.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: order-service            # ← Must match the resource in base
spec:
  template:
    spec:
      containers:
        - name: order-service    # ← Must match container name
          resources:
            limits:
              memory: 1Gi        # ← Only this field changes
```

**How merge works:**

```
BASE                                    PATCH                           RESULT
spec:                                   spec:                           spec:
  replicas: 1                             template:                       replicas: 1        ← kept
  template:                                 spec:                         template:
    spec:                                     containers:                   spec:
      containers:                               - name: order-service         containers:
        - name: order-service                     resources:                    - name: order-service
          image: order-service:v1                   limits:                       image: order-service:v1  ← kept
          resources:                                  memory: 1Gi                 resources:
            limits:                                                                limits:
              memory: 512Mi                                                          memory: 1Gi  ← CHANGED
              cpu: 500m                                                              cpu: 500m    ← kept
```

### JSON Patch (For Precision)

```yaml
# In kustomization.yaml
patches:
  - target:
      kind: Deployment
      name: order-service
    patch: |
      - op: replace
        path: /spec/replicas
        value: 3
      - op: add
        path: /metadata/annotations/custom
        value: "hello"
      - op: remove
        path: /spec/template/spec/containers/0/resources/limits/cpu
```

| Operation | What it does | Example |
|-----------|-------------|---------|
| `add` | Add a new field or array element | Add an annotation |
| `remove` | Delete a field | Remove a resource limit |
| `replace` | Change an existing field's value | Change replicas |
| `move` | Move a field | Rare |
| `copy` | Copy a field | Rare |
| `test` | Assert a value exists (fails if not) | Validation |

### When to Use Which

```
Strategic Merge Patch               JSON Patch
─────────────────────               ──────────
✓ Changing field values             ✓ Removing fields
✓ Adding new fields                 ✓ Array manipulation by index
✓ Readable, looks like K8s YAML     ✓ Precise, unambiguous operations
✗ Can't remove fields               ✗ Verbose, harder to read
✗ Array merge is tricky             ✗ Must know exact JSON path
```

---

## 5. ConfigMap & Secret Generators

### Why Generators Instead of Static YAML?

Generators add a **content hash suffix** to names (e.g., `app-config-k8m2d5h`).
When content changes, the name changes, which triggers a **rolling update** of pods
referencing it — K8s sees a new ConfigMap name and restarts pods.

```yaml
# kustomization.yaml
configMapGenerator:
  - name: app-config
    literals:
      - KAFKA_SERVERS=kafka:9092
      - REDIS_CONFIG=redis:6379
    files:
      - application.properties    # Entire file becomes a key

secretGenerator:
  - name: db-creds
    literals:
      - DB_PASSWORD=secret123
    options:
      disableNameSuffixHash: true  # Don't add hash (not recommended)
```

### Generated Output

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: app-config-k8m2d5h        # ← Hash suffix auto-added
data:
  KAFKA_SERVERS: kafka:9092
  REDIS_CONFIG: redis:6379
  application.properties: |
    server.port=8080
    ...
```

### Hash Suffix Behavior

```
Change config value → New hash → New ConfigMap name → Pods restart
Don't change        → Same hash → Same name         → No restart

This gives you ATOMIC config updates — old pods keep old config until
they're replaced by new pods with the new config.
```

---

## 6. Common Transformers

### namespace

```yaml
namespace: order-system
# Adds to EVERY resource:
#   metadata:
#     namespace: order-system
```

### namePrefix / nameSuffix

```yaml
namePrefix: dev-
# redis → dev-redis
# order-service → dev-order-service
# Also updates all references (Service selectors, ConfigMap refs, etc.)
```

### commonLabels

```yaml
commonLabels:
  env: dev
  team: backend
```

**Warning:** `commonLabels` adds labels to `spec.selector.matchLabels` in Deployments.
Selectors are **immutable** after creation. If you add `commonLabels` to an existing
deployment, you must delete and recreate it.

### images

```yaml
images:
  - name: order-system/store-api       # Match by current image name
    newTag: v1.2.3                      # Only change the tag
  - name: order-system/order-service
    newName: ghcr.io/myorg/order-svc    # Change registry + name
    newTag: sha-abc1234
```

This is the **most important field for GitOps** — CI pipelines update image tags here,
ArgoCD detects the change, and deploys the new version.

---

## 7. Components — Reusable Mixins

Components are reusable pieces that can be **included in multiple overlays**.

```
k8s/
├── base/
├── components/
│   ├── monitoring/                # Adds Prometheus annotations
│   │   └── kustomization.yaml
│   └── linkerd/                   # Adds Linkerd injection annotation
│       └── kustomization.yaml
├── overlays/
│   ├── dev/                       # Uses: base + linkerd
│   └── prod/                      # Uses: base + linkerd + monitoring
```

```yaml
# components/linkerd/kustomization.yaml
apiVersion: kustomize.config.k8s.io/v1alpha1   # Note: v1alpha1
kind: Component
patches:
  - target:
      kind: Namespace
    patch: |
      - op: add
        path: /metadata/annotations/linkerd.io~1inject
        value: enabled
```

```yaml
# overlays/prod/kustomization.yaml
resources:
  - ../../base
components:
  - ../../components/linkerd
  - ../../components/monitoring
```

---

## 8. Kustomize vs Helm

| Aspect | Kustomize | Helm |
|--------|-----------|------|
| **Philosophy** | Patch real YAML | Template with Go `{{ }}` |
| **Learning curve** | Low — just K8s YAML + patches | Medium — Go templates + values.yaml |
| **Logic** | None (no if/else/loops) | Full Go template language |
| **Packaging** | Directory of files | Chart (.tgz archive) |
| **Sharing** | Copy directories | Helm repo (like npm registry) |
| **K8s native** | Built into `kubectl` | Separate CLI + Tiller (v2) / SDK (v3) |
| **Best for** | Your own apps, simple overrides | Third-party apps (Prometheus, Nginx) |
| **ArgoCD support** | ✅ Auto-detected | ✅ Supported |
| **Composition** | Overlay on overlay | Chart dependencies |

**Rule of thumb:**
- **Your services** (StoreApi, OrderService, InventoryWorker) → **Kustomize**
- **Third-party software** (Kafka, Redis, monitoring stack) → **Helm charts**
- You can mix both — Helm to install Kafka, Kustomize for your apps

---

## 9. Directory Structure Patterns

### Pattern A: Simple (Start Here)

```
k8s/
├── base/
│   ├── kustomization.yaml
│   ├── store-api.yaml
│   ├── order-service.yaml
│   └── inventory-worker.yaml
└── overlays/
    ├── dev/
    │   └── kustomization.yaml
    └── prod/
        └── kustomization.yaml
```

### Pattern B: Layered (Medium Complexity)

```
k8s/
├── base/
│   ├── kustomization.yaml
│   ├── infra/                   # Databases, message bus
│   │   ├── kustomization.yaml
│   │   ├── redis.yaml
│   │   ├── postgres.yaml
│   │   └── kafka.yaml
│   └── apps/                    # Your services
│       ├── kustomization.yaml
│       ├── store-api.yaml
│       ├── order-service.yaml
│       └── inventory-worker.yaml
├── components/                  # Reusable add-ons
│   ├── linkerd/
│   └── monitoring/
└── overlays/
    ├── dev/
    │   ├── kustomization.yaml
    │   └── patches/
    └── prod/
        ├── kustomization.yaml
        └── patches/
```

### Pattern C: Per-App (Large Teams)

```
k8s/
├── store-api/
│   ├── base/
│   └── overlays/
├── order-service/
│   ├── base/
│   └── overlays/
└── inventory-worker/
    ├── base/
    └── overlays/
```

---

## 10. How ArgoCD Uses Kustomize

ArgoCD **auto-detects** Kustomize when it finds a `kustomization.yaml` in the source path.

```
ArgoCD Application
  source:
    path: k8s/overlays/dev/          ← ArgoCD sees kustomization.yaml here
                                      ← Internally runs: kubectl kustomize k8s/overlays/dev/
                                      ← Applies the output to the cluster
```

When you change a Kustomize file in Git:
1. ArgoCD polls Git (every 3 min) or gets webhook notification
2. Runs `kustomize build` on the path
3. Compares output to live cluster state
4. If different → marks as "OutOfSync"
5. If auto-sync enabled → applies the diff

---

## 11. Common Mistakes & Gotchas

| Mistake | What Happens | Fix |
|---------|-------------|-----|
| Patch `metadata.name` doesn't match base | Patch is silently ignored | Double-check names |
| Using `commonLabels` after first deploy | Selector mismatch error | Delete deployment first |
| Forgetting `namespace` in overlay | Resources go to `default` | Add `namespace:` field |
| Listing a file not in `resources` | Kustomize ignores it | Add to `resources:` list |
| Circular base references | Infinite loop error | Check directory refs |
| Using `:latest` tag | Can't tell which version is deployed | Use specific tags |

---

## 12. Command Reference

```bash
# Preview rendered YAML (doesn't apply anything)
kubectl kustomize k8s/overlays/dev/

# Apply to cluster
kubectl apply -k k8s/overlays/dev/

# Delete all resources managed by this kustomization
kubectl delete -k k8s/overlays/dev/

# Diff against live cluster
kubectl diff -k k8s/overlays/dev/

# Pipe to a file (useful for debugging)
kubectl kustomize k8s/overlays/dev/ > rendered.yaml

# Edit a kustomization (adds resource entry)
cd k8s/base && kustomize edit add resource new-service.yaml

# Set image tag (CI pipelines use this)
cd k8s/overlays/dev && kustomize edit set image order-system/order-service:v1.2.3
```
