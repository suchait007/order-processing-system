# KT-09: GitOps — Principles, Patterns & Mental Models
**Author:** Copilot KT Session  
**Created:** 2026-06-05  
**Audience:** Developers adopting GitOps for Kubernetes deployments  
**Status:** Living Document

---

## Table of Contents
1. [What GitOps Actually Is](#1-what-gitops-actually-is)
2. [The Four Principles](#2-the-four-principles)
3. [Push vs Pull Model](#3-push-vs-pull-model)
4. [Desired State vs Live State](#4-desired-state-vs-live-state)
5. [Reconciliation Loop](#5-reconciliation-loop)
6. [Repository Strategies](#6-repository-strategies)
7. [Branching Strategies for GitOps](#7-branching-strategies-for-gitops)
8. [CI vs CD — The Separation](#8-ci-vs-cd--the-separation)
9. [Secret Management in GitOps](#9-secret-management-in-gitops)
10. [Rollback — Git Revert = Infra Revert](#10-rollback--git-revert--infra-revert)
11. [Drift Detection & Self-Healing](#11-drift-detection--self-healing)
12. [Multi-Environment Promotion](#12-multi-environment-promotion)
13. [GitOps Anti-Patterns](#13-gitops-anti-patterns)
14. [GitOps vs Traditional CD](#14-gitops-vs-traditional-cd)

---

## 1. What GitOps Actually Is

GitOps is an **operational framework** where:
- **Git** is the single source of truth for your infrastructure and application config
- A **software agent** (ArgoCD, Flux) continuously ensures the cluster matches Git
- **All changes** go through Git (pull requests, reviews, audit trail)

```
┌──────────────────────────────────────────────────────────────────┐
│                                                                   │
│  The core idea:                                                   │
│                                                                   │
│  "If it's not in Git, it doesn't exist."                         │
│  "If it IS in Git, it MUST exist in the cluster."                │
│                                                                   │
└──────────────────────────────────────────────────────────────────┘
```

### What GitOps Is NOT

| Misconception | Reality |
|---------------|---------|
| "GitOps = using Git" | No — most teams use Git already. GitOps is about an **agent** reconciling Git → Cluster |
| "GitOps = CI/CD" | No — GitOps is specifically the **CD** part. CI is separate |
| "GitOps = ArgoCD" | No — ArgoCD is **one implementation** of GitOps (Flux is another) |
| "GitOps = Kubernetes only" | Mostly yes today, but the pattern applies to any declarative system |

---

## 2. The Four Principles

Defined by the OpenGitOps project (CNCF):

### Principle 1: Declarative

The entire system must be described **declaratively**.

```
IMPERATIVE (bad for GitOps):          DECLARATIVE (good for GitOps):
─────────────────────────             ────────────────────────────
kubectl scale deploy X --replicas=3   # deployment.yaml
kubectl set image deploy X img=v2     spec:
kubectl create configmap ...            replicas: 3
                                        image: my-app:v2
(commands — "do this")                (state — "I want this")
```

**Your system:** All your K8s YAML files (Deployments, Services, ConfigMaps) are
declarative. That's why Kubernetes and GitOps work so well together.

### Principle 2: Versioned and Immutable

The desired state is stored in a way that enforces **immutability and versioning** —
Git provides both naturally.

```
$ git log --oneline
a3f2b1c Scale order-service to 3 replicas
9d4e5f6 Add Redis connection pool config
7b8c9d0 Initial deployment manifests

Every change is:
  ✓ Versioned (commit SHA)
  ✓ Immutable (can't modify a commit)
  ✓ Auditable (who, when, what, why)
  ✓ Reviewable (pull requests)
  ✓ Revertable (git revert)
```

### Principle 3: Pulled Automatically

Software agents **automatically pull** the desired state from Git and apply it.

```
PUSH MODEL (traditional CI/CD):
  CI server ──kubectl apply──► Cluster
  (CI has cluster credentials — security risk)

PULL MODEL (GitOps):
  Agent (in cluster) ──polls──► Git repo
  Agent ──applies──► Cluster
  (Agent already has cluster access — no credentials leave the cluster)
```

### Principle 4: Continuously Reconciled

The agent continuously compares **desired state** (Git) with **live state** (cluster)
and corrects any differences (**drift**).

```
┌──────────────────────────────────────────────────┐
│            RECONCILIATION LOOP                    │
│                                                   │
│  ┌──────┐   desired    ┌──────────┐              │
│  │ Git  │─────────────►│  Agent   │              │
│  │ Repo │              │ (ArgoCD) │              │
│  └──────┘              └────┬─────┘              │
│                              │                    │
│                        compare & fix              │
│                              │                    │
│                         ┌────▼─────┐              │
│                         │ Cluster  │              │
│                         │ (live)   │              │
│                         └──────────┘              │
│                                                   │
│  If live ≠ desired → fix it (self-heal)          │
│  If live = desired → do nothing (steady state)   │
└──────────────────────────────────────────────────┘
```

---

## 3. Push vs Pull Model

### Push Model (Traditional CI/CD)

```
Developer → git push → CI Pipeline → kubectl apply → Cluster
                            │
                    CI needs cluster credentials
                    CI is a single point of failure
                    If CI is down, no deployments
                    If CI is compromised, cluster is compromised
```

### Pull Model (GitOps)

```
Developer → git push → Git Repo (passive)
                            ▲
                            │ polls every N minutes
                            │
                     ┌──────┴──────┐
                     │   ArgoCD    │──── applies ────► Cluster
                     │ (in cluster)│
                     └─────────────┘
                     Already has access
                     Self-healing
                     No external credentials needed
```

### Why Pull Is Better

| Concern | Push | Pull |
|---------|------|------|
| **Credentials** | CI needs kubectl access | Agent already inside cluster |
| **Failure mode** | CI down = no deploys | Agent down = deploys resume on restart |
| **Drift detection** | None — only applies once | Continuous comparison |
| **Security** | Cluster creds in CI env | No creds leave the cluster |
| **Audit** | CI logs (may be lost) | Git history (permanent) |

---

## 4. Desired State vs Live State

This is the most important mental model in GitOps.

```
┌─────────────────────────────────────────────────────────────┐
│                                                              │
│  DESIRED STATE (Git)           LIVE STATE (Cluster)          │
│  ══════════════════            ═══════════════════           │
│                                                              │
│  deployment.yaml:              kubectl get deploy:           │
│    replicas: 3                   replicas: 3     ✓ Match     │
│    image: v1.2.3                 image: v1.2.3   ✓ Match     │
│    memory: 512Mi                 memory: 512Mi   ✓ Match     │
│                                                              │
│  STATUS: Synced ✅                                           │
│                                                              │
│  ─────── Someone runs: kubectl scale --replicas=5 ────────   │
│                                                              │
│  deployment.yaml:              kubectl get deploy:           │
│    replicas: 3                   replicas: 5     ✗ DRIFT     │
│    image: v1.2.3                 image: v1.2.3   ✓ Match     │
│    memory: 512Mi                 memory: 512Mi   ✓ Match     │
│                                                              │
│  STATUS: OutOfSync ⚠️                                        │
│  ArgoCD action: revert to 3 replicas (self-heal)            │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

---

## 5. Reconciliation Loop

The reconciliation loop is the engine of GitOps:

```
EVERY 3 MINUTES (default ArgoCD poll interval):

  1. FETCH      Pull latest from Git repo
  2. RENDER     Run kustomize build (or helm template)
  3. COMPARE    Diff rendered YAML vs live cluster objects
  4. DECIDE     Is there a difference?
                  YES → OutOfSync → Apply if auto-sync enabled
                  NO  → Synced → Do nothing
  5. REPORT     Update Application status in ArgoCD UI

CONTINUOUSLY (via K8s watch API):

  6. WATCH      Monitor live cluster for manual changes
  7. DETECT     If live state changes and differs from Git
  8. ALERT      Mark as OutOfSync
  9. HEAL       Revert if selfHeal enabled
```

### Convergence vs Eventual Consistency

GitOps is **eventually consistent** — there's a window between `git push` and the
cluster updating. This window is typically:
- **3 minutes** with polling (default)
- **< 10 seconds** with webhooks (GitHub webhook → ArgoCD)

---

## 6. Repository Strategies

### Strategy A: Mono-Repo

```
order-system/
├── src/                      # Application code
│   ├── StoreApi/
│   ├── OrderService/
│   └── InventoryWorker/
├── k8s/                      # K8s config (GitOps target)
│   ├── base/
│   └── overlays/
└── Dockerfiles
```

| Pros | Cons |
|------|------|
| Simple to start | App code commits trigger ArgoCD (noise) |
| Everything in one place | Different team access needs |
| Easy cross-references | CI and CD coupled |

### Strategy B: Two-Repo (Recommended for Production)

```
┌──────────────────────┐       ┌──────────────────────┐
│  app-repo/           │       │  config-repo/         │
│  ├── StoreApi/       │       │  ├── base/            │
│  ├── OrderService/   │       │  ├── overlays/        │
│  └── InventoryWorker/│       │  │   ├── dev/         │
│                      │       │  │   └── prod/        │
│  CI: build + test    │──────►│  └── argocd/          │
│      + docker push   │ bot   │                       │
│                      │commits│  ArgoCD watches THIS  │
└──────────────────────┘ image └──────────────────────┘
                          tag
```

| Pros | Cons |
|------|------|
| Clean separation of concerns | Two repos to manage |
| App commits don't trigger deploys | Need automation to bridge repos |
| Different permissions | More initial setup |
| Clear audit trail per concern | |

### Strategy C: Per-Environment Repos (Large Orgs)

```
config-dev/       ← ArgoCD dev cluster watches this
config-staging/   ← ArgoCD staging cluster watches this
config-prod/      ← ArgoCD prod cluster watches this (with approvals)
```

**Start with Mono-Repo (Strategy A)** for learning. Move to Two-Repo (Strategy B)
when you understand the workflow.

---

## 7. Branching Strategies for GitOps

### Option 1: Branch-Per-Environment (Simple but Risky)

```
main          ← prod config
├── dev       ← dev config
└── staging   ← staging config
```

**Problem:** Cherry-picking and merge conflicts between branches.

### Option 2: Directory-Per-Environment (Recommended)

```
main branch only
├── k8s/overlays/dev/       ← Dev config
├── k8s/overlays/staging/   ← Staging config
└── k8s/overlays/prod/      ← Prod config
```

**This is what Kustomize overlays are designed for.**
Each ArgoCD Application points to a different directory on the same branch.

### Option 3: Tag/Release-Based (For Prod)

```
main     ← dev deploys from HEAD
v1.2.3   ← prod ArgoCD targets this specific tag
```

Promotion = creating a new Git tag → ArgoCD syncs to that tag.

---

## 8. CI vs CD — The Separation

```
┌─────────────────────────────────────────────────────────────────┐
│                                                                  │
│  CI (Continuous Integration)    CD (Continuous Delivery)         │
│  ═══════════════════════        ══════════════════════          │
│  Owned by: GitHub Actions       Owned by: ArgoCD                │
│  Triggered by: code push        Triggered by: config change     │
│  Does: test, build, push image  Does: sync Git → cluster        │
│  Outputs: Docker image          Outputs: running pods           │
│                                                                  │
│  ┌──────────┐   ┌──────────┐   ┌──────────┐   ┌────────────┐  │
│  │ git push │──►│ CI Build │──►│ Update   │──►│  ArgoCD    │  │
│  │ (code)   │   │ & Test   │   │ image tag│   │  syncs     │  │
│  └──────────┘   └──────────┘   │ in Git   │   │  to K8s    │  │
│                                 └──────────┘   └────────────┘  │
│                                  (config repo                   │
│                                   commit)                       │
│                                                                  │
│  CI NEVER does kubectl apply.                                   │
│  CI's job ends at: docker push + update image tag in Git.       │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Example CI Pipeline for OrderService

```yaml
# .github/workflows/ci.yaml (in app-repo)
name: CI
on:
  push:
    paths: ['OrderService/**']

jobs:
  build:
    steps:
      - run: dotnet test
      - run: docker build -t ghcr.io/you/order-service:${{ github.sha }} .
      - run: docker push ghcr.io/you/order-service:${{ github.sha }}

      # The GitOps bridge: update image tag in config repo
      - run: |
          git clone config-repo
          cd config-repo/k8s/overlays/dev
          kustomize edit set image order-service=ghcr.io/you/order-service:${{ github.sha }}
          git commit -am "chore: update order-service to ${{ github.sha }}"
          git push
```

Notice: **CI never touches kubectl**. It only updates a file in Git.
ArgoCD picks up the change and deploys.

---

## 9. Secret Management in GitOps

**Problem:** GitOps says "everything in Git" — but you can't commit passwords to Git.

### Solutions

| Tool | How It Works | Complexity |
|------|-------------|------------|
| **Sealed Secrets** | Encrypts secrets with cluster public key → safe to commit | Low |
| **SOPS** | Encrypts YAML values with KMS/PGP key | Medium |
| **External Secrets Operator** | K8s operator pulls from Vault/AWS/Azure | Medium |
| **HashiCorp Vault** | Full secret management platform | High |

### Sealed Secrets (Simplest for Learning)

```bash
# Install
kubectl apply -f https://github.com/bitnami-labs/sealed-secrets/releases/download/v0.24.0/controller.yaml

# Encrypt a secret
kubectl create secret generic db-creds \
  --from-literal=password=ChangeMeNow123! \
  --dry-run=client -o yaml | kubeseal -o yaml > sealed-secret.yaml

# sealed-secret.yaml is safe to commit to Git!
# Only the cluster's controller can decrypt it.
```

---

## 10. Rollback — Git Revert = Infra Revert

One of the most powerful GitOps concepts:

```
TRADITIONAL ROLLBACK:                 GITOPS ROLLBACK:
─────────────────────                 ────────────────────
1. SSH into CI server                 1. git revert HEAD
2. Find old pipeline run              2. git push
3. Re-run with old parameters         3. Done.
4. Hope it works
5. No audit trail                     Full audit trail.
                                      ArgoCD handles the rest.
```

```bash
# Something broke after last deploy
git log --oneline
# abc1234 feat: update order-service to v2.0.0  ← This broke things
# def5678 chore: update redis config

git revert abc1234
git push

# ArgoCD detects the revert commit
# Syncs cluster back to the previous state
# order-service rolls back to v1.9.0
```

---

## 11. Drift Detection & Self-Healing

### What Is Drift?

Drift = live cluster state differs from Git state.

```
Common causes of drift:
  1. Someone runs: kubectl edit deployment order-service
  2. Someone runs: kubectl scale --replicas=5
  3. A K8s admission controller modifies resources
  4. An operator manages the same resources

ArgoCD detects all of these.
```

### Self-Healing Behavior

```yaml
# ArgoCD Application spec
syncPolicy:
  automated:
    selfHeal: true     # ← Revert manual changes automatically
    prune: true        # ← Delete resources not in Git
```

| Setting | Behavior |
|---------|----------|
| `selfHeal: false` | Marks drift as "OutOfSync" but doesn't fix |
| `selfHeal: true` | Automatically reverts to Git state |
| `prune: false` | Old resources remain even if removed from Git |
| `prune: true` | Deletes resources removed from Git |

---

## 12. Multi-Environment Promotion

### Promotion Flow

```
┌───────┐    merge     ┌──────────┐    PR/approve    ┌──────────┐
│  Dev  │────────────►│ Staging   │─────────────────►│   Prod   │
│       │              │           │                   │          │
│ auto- │              │ auto-sync │                   │ manual   │
│ sync  │              │           │                   │ sync     │
└───────┘              └──────────┘                   └──────────┘

k8s/overlays/dev/      k8s/overlays/staging/          k8s/overlays/prod/
image: :sha-abc        image: :v1.2.3                  image: :v1.2.3
replicas: 1            replicas: 2                     replicas: 5
```

### Promotion Patterns

**Pattern A — Image Tag Promotion:**
1. Dev uses commit SHA tags (`:sha-abc1234`)
2. Staging uses release candidates (`:v1.2.3-rc1`)
3. Prod uses release tags (`:v1.2.3`)

**Pattern B — PR-Based Promotion:**
1. Change lands in `k8s/overlays/dev/` → auto-deployed
2. PR from dev overlay changes to staging overlay → review → merge → deployed
3. PR from staging overlay changes to prod overlay → approval → merge → deployed

**Pattern C — ArgoCD ApplicationSets (Advanced):**
ArgoCD generates Applications dynamically from a template + Git directory structure.

---

## 13. GitOps Anti-Patterns

| Anti-Pattern | Why It's Bad | Do This Instead |
|-------------|-------------|-----------------|
| `kubectl apply` from laptop | Bypasses Git, no audit | Always commit to Git |
| CI pipeline does `kubectl apply` | Push model, creds in CI | CI updates Git, ArgoCD pulls |
| Editing live resources manually | Drift, overwritten by sync | Edit in Git, push |
| Storing plaintext secrets in Git | Security breach | Sealed Secrets / SOPS / Vault |
| One branch per environment | Merge conflicts, drift | Directory per environment |
| No pull request reviews for config | Risky changes bypass review | Require PR review |
| Auto-sync in prod without gates | Dangerous: any merge deploys | Manual sync or approval |

---

## 14. GitOps vs Traditional CD

```
┌────────────────────────────────────────────────────────────────┐
│                                                                 │
│  TRADITIONAL CD                    GITOPS                       │
│  ══════════════                    ═════                        │
│                                                                 │
│  "Deploy button" in CI UI         git push (or merge PR)       │
│  CI runs kubectl apply             ArgoCD reconciles            │
│  State lives in CI config          State lives in Git           │
│  Rollback = re-run old pipeline    Rollback = git revert       │
│  "Who deployed?" → check CI logs   "Who deployed?" → git log   │
│  Cluster creds in CI env           Creds stay in cluster       │
│  No drift detection                Continuous drift detection  │
│  "Apply and forget"                "Declare and converge"       │
│                                                                 │
└────────────────────────────────────────────────────────────────┘
```
