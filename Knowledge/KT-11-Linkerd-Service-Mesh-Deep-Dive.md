# KT-11: Linkerd — Service Mesh Deep Dive
**Author:** Copilot KT Session  
**Created:** 2026-06-05  
**Audience:** Developers adding observability and security to Kubernetes microservices  
**Status:** Living Document

---

## Table of Contents
1. [What a Service Mesh Is](#1-what-a-service-mesh-is)
2. [Why Linkerd Over Istio](#2-why-linkerd-over-istio)
3. [Architecture — Control Plane & Data Plane](#3-architecture--control-plane--data-plane)
4. [The Sidecar Proxy — How Injection Works](#4-the-sidecar-proxy--how-injection-works)
5. [mTLS — Mutual TLS Everywhere](#5-mtls--mutual-tls-everywhere)
6. [Observability — Golden Metrics](#6-observability--golden-metrics)
7. [Service Profiles — Per-Route Intelligence](#7-service-profiles--per-route-intelligence)
8. [Traffic Management — Retries, Timeouts, Splits](#8-traffic-management--retries-timeouts-splits)
9. [Multi-Cluster & Service Mirroring](#9-multi-cluster--service-mirroring)
10. [Authorization Policies](#10-authorization-policies)
11. [Extensions — Viz, Jaeger, Multicluster](#11-extensions--viz-jaeger-multicluster)
12. [How Linkerd Integrates with Your Order System](#12-how-linkerd-integrates-with-your-order-system)
13. [Debugging with Linkerd](#13-debugging-with-linkerd)
14. [Performance Impact](#14-performance-impact)
15. [Common Mistakes & Gotchas](#15-common-mistakes--gotchas)
16. [Command Reference](#16-command-reference)

---

## 1. What a Service Mesh Is

### The Problem

In a microservice architecture, every service talks to other services over the network.
This creates cross-cutting concerns:

```
Without a service mesh, EVERY service must implement:

  ┌──────────────────────────────────────────────────────┐
  │  OrderService                                         │
  │                                                       │
  │  ✗ TLS certificate management                        │
  │  ✗ Retry logic (Polly — you already have this!)      │
  │  ✗ Circuit breaker                                    │
  │  ✗ Timeout management                                │
  │  ✗ Metrics collection (latency, success rate)        │
  │  ✗ Distributed tracing                                │
  │  ✗ Load balancing                                     │
  │  ✗ Access control (who can call me?)                 │
  │                                                       │
  │  + The actual business logic (placing orders)         │
  └──────────────────────────────────────────────────────┘

  Every service duplicates this infrastructure code.
  Different languages? Different implementations.
  Bug in retry logic? Fix in every service.
```

### The Solution

```
With a service mesh, the PROXY handles cross-cutting concerns:

  ┌──────────────────────────────────────────────────────┐
  │  Pod                                                  │
  │  ┌──────────────────┐  ┌──────────────────────────┐  │
  │  │  OrderService    │  │  Linkerd Proxy (sidecar)  │  │
  │  │                  │  │                            │  │
  │  │  Only business   │──│  ✓ mTLS                   │  │
  │  │  logic!          │  │  ✓ Retries                │  │
  │  │                  │  │  ✓ Timeouts               │  │
  │  │  No infra code   │  │  ✓ Metrics                │  │
  │  │  needed          │  │  ✓ Load balancing         │  │
  │  │                  │  │  ✓ Access control          │  │
  │  └──────────────────┘  └──────────────────────────┘  │
  └──────────────────────────────────────────────────────┘

  Linkerd proxy intercepts ALL network traffic transparently.
  Your application code doesn't know it's there.
```

### Key Insight

A service mesh operates at **Layer 7 (HTTP)** and **Layer 4 (TCP)**. It understands
HTTP methods, paths, status codes, and gRPC — not just raw bytes.

---

## 2. Why Linkerd Over Istio

| Aspect | Linkerd | Istio |
|--------|---------|-------|
| **Complexity** | Simple — fewer moving parts | Complex — many components |
| **Resource usage** | ~20MB RAM per proxy | ~100MB+ RAM per proxy (Envoy) |
| **Proxy** | linkerd2-proxy (Rust) | Envoy (C++) |
| **Latency added** | < 1ms p99 | ~3-5ms p99 |
| **Learning curve** | Days | Weeks |
| **CRDs** | ~5 | ~50+ |
| **CNCF status** | Graduated ✅ | Graduated ✅ |
| **Best for** | Most teams, simpler setups | Complex routing, multi-protocol |
| **Config language** | YAML (simple) | YAML (complex, Envoy filters) |

**Rule of thumb:** Start with Linkerd. Move to Istio only if you need Envoy-specific
features (WASM filters, complex traffic routing, non-HTTP protocols).

---

## 3. Architecture — Control Plane & Data Plane

```
┌─────────────────────────────────────────────────────────────────┐
│                    LINKERD ARCHITECTURE                          │
│                                                                  │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │                  CONTROL PLANE (linkerd-system namespace) │  │
│  │                                                            │  │
│  │  ┌─────────────────┐   ┌──────────────┐  ┌────────────┐  │  │
│  │  │  destination     │   │  identity     │  │  proxy-    │  │  │
│  │  │                  │   │               │  │  injector  │  │  │
│  │  │  Service         │   │  Certificate  │  │            │  │  │
│  │  │  discovery       │   │  authority    │  │  Mutating  │  │  │
│  │  │  (where to send  │   │  (issues TLS  │  │  webhook   │  │  │
│  │  │   traffic)       │   │   certs to    │  │  (injects  │  │  │
│  │  │                  │   │   proxies)    │  │   sidecars)│  │  │
│  │  └─────────────────┘   └──────────────┘  └────────────┘  │  │
│  │                                                            │  │
│  │  ┌─────────────────┐                                       │  │
│  │  │  heartbeat       │   Sends anonymous usage stats        │  │
│  │  └─────────────────┘                                       │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │                  DATA PLANE (your namespaces)              │  │
│  │                                                            │  │
│  │  ┌────────────────────────────────────────────┐           │  │
│  │  │  Pod: order-service                         │           │  │
│  │  │  ┌──────────────┐  ┌─────────────────────┐ │           │  │
│  │  │  │  App          │  │  linkerd-proxy       │ │           │  │
│  │  │  │  Container    │──│  (sidecar)           │ │           │  │
│  │  │  │              │  │  - Intercepts traffic │ │           │  │
│  │  │  │              │  │  - Encrypts with mTLS │ │           │  │
│  │  │  │              │  │  - Collects metrics   │ │           │  │
│  │  │  │              │  │  - Applies policies   │ │           │  │
│  │  │  └──────────────┘  └─────────────────────┘ │           │  │
│  │  └────────────────────────────────────────────┘           │  │
│  │                                                            │  │
│  │  ┌────────────────────────────────────────────┐           │  │
│  │  │  Pod: store-api                             │           │  │
│  │  │  ┌──────────────┐  ┌─────────────────────┐ │           │  │
│  │  │  │  App          │  │  linkerd-proxy       │ │           │  │
│  │  │  │  Container    │──│  (sidecar)           │ │           │  │
│  │  │  └──────────────┘  └─────────────────────┘ │           │  │
│  │  └────────────────────────────────────────────┘           │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

### Component Roles

| Component | Role | Analogy |
|-----------|------|---------|
| **destination** | Service discovery + routing policies | DNS + load balancer config |
| **identity** | Certificate Authority — issues mTLS certs | Internal PKI |
| **proxy-injector** | Automatically injects sidecar into pods | Admission controller |
| **linkerd-proxy** | Per-pod data plane proxy | nginx/envoy per pod |

---

## 4. The Sidecar Proxy — How Injection Works

### What Happens When a Pod Starts

```
1. You create a Deployment in a namespace with linkerd.io/inject: enabled
2. K8s API receives the Pod spec
3. Linkerd's mutating admission webhook intercepts
4. Webhook modifies the Pod spec:
   - Adds linkerd-proxy container (the sidecar)
   - Adds linkerd-init container (iptables rules)
   - Configures networking to route through proxy
5. Modified Pod is scheduled

┌──────────────────────────────────────────────────────────────┐
│  Pod (after injection)                                        │
│                                                               │
│  Init Containers (run first, then exit):                      │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │  linkerd-init                                             │ │
│  │  Configures iptables/nft rules to redirect ALL traffic    │ │
│  │  through the proxy:                                       │ │
│  │    - Inbound port 5200 → proxy port 4143 → app port 5200 │ │
│  │    - Outbound app → proxy port 4140 → destination         │ │
│  └─────────────────────────────────────────────────────────┘ │
│                                                               │
│  Containers (run simultaneously):                             │
│  ┌─────────────────┐      ┌─────────────────────────────┐   │
│  │  order-service   │      │  linkerd-proxy               │   │
│  │  (your app)      │◄────►│  (transparent L7 proxy)      │   │
│  │                  │      │                               │   │
│  │  Listens on 5200 │      │  Inbound:  4143              │   │
│  │  Calls store-api │      │  Outbound: 4140              │   │
│  │  on port 5116    │      │  Admin:    4191              │   │
│  │                  │      │  Metrics:  4191/metrics      │   │
│  └─────────────────┘      └─────────────────────────────┘   │
└──────────────────────────────────────────────────────────────┘
```

### Traffic Flow (After Injection)

```
BEFORE Linkerd:
  OrderService ──HTTP (plaintext)──► StoreApi:5116

AFTER Linkerd:
  OrderService
      │
      │ app thinks it's sending to store-api:5116
      ▼
  linkerd-proxy (in order-service pod)
      │
      │ encrypted with mTLS, metrics collected
      ▼
  linkerd-proxy (in store-api pod)
      │
      │ decrypted, forwarded to app
      ▼
  StoreApi (receives plain HTTP on 5116 — unchanged!)

YOUR APPLICATION CODE CHANGES: ZERO
```

### Injection Methods

```yaml
# Method 1: Namespace-wide (recommended)
apiVersion: v1
kind: Namespace
metadata:
  name: order-system
  annotations:
    linkerd.io/inject: enabled       # All pods in this namespace

# Method 2: Per-Deployment
apiVersion: apps/v1
kind: Deployment
metadata:
  name: order-service
  annotations:
    linkerd.io/inject: enabled       # Only this deployment

# Method 3: CLI (one-time, for testing)
kubectl get deploy order-service -o yaml | linkerd inject - | kubectl apply -f -

# Method 4: Opt-out specific pods
metadata:
  annotations:
    linkerd.io/inject: disabled      # Skip injection for this pod
```

---

## 5. mTLS — Mutual TLS Everywhere

### What Is mTLS?

```
Regular TLS (HTTPS):
  Client ──────────────► Server
  Client verifies server's certificate
  Server doesn't verify client

Mutual TLS (mTLS):
  Client ◄──────────────► Server
  Client verifies server's certificate  ✓
  Server verifies client's certificate  ✓
  Both parties prove their identity
```

### How Linkerd Does mTLS

```
┌─────────────────────────────────────────────────────────────┐
│  1. Pod starts → proxy requests certificate from identity   │
│  2. Identity (CA) issues short-lived cert (24h default)     │
│  3. Cert contains pod's identity:                           │
│     spiffe://cluster.local/ns/order-system/sa/order-service │
│  4. Proxy auto-renews before expiry                         │
│  5. Every connection between meshed pods uses mTLS           │
│                                                              │
│  You configure NOTHING. It's automatic.                      │
└─────────────────────────────────────────────────────────────┘
```

### SPIFFE Identity

Every workload gets a cryptographic identity in SPIFFE format:

```
spiffe://cluster.local/ns/<namespace>/sa/<service-account>

Examples:
  spiffe://cluster.local/ns/order-system/sa/order-service
  spiffe://cluster.local/ns/order-system/sa/store-api
  spiffe://cluster.local/ns/order-system/sa/inventory-worker
```

This identity is used for:
- Mutual authentication (prove who you are)
- Authorization policies (who can call whom)
- Audit logging (which service made the request)

### Verifying mTLS

```bash
# Check if mTLS is active between services
linkerd viz edges deployment -n order-system

# Output:
# SRC                DST             SRC_IDENTITY                                        DST_IDENTITY                                        TLS
# order-service      store-api       order-service.order-system.serviceaccount.identity   store-api.order-system.serviceaccount.identity       true
# order-service      kafka           order-service.order-system.serviceaccount.identity   (not meshed)                                        false
# inventory-worker   kafka           inventory-worker.order-system.serviceaccount.identity (not meshed)                                       false

# Note: Kafka isn't meshed (no sidecar) so mTLS is one-sided
# Between your .NET services → full mTLS ✓
```

---

## 6. Observability — Golden Metrics

Linkerd automatically collects the **four golden signals** for every request:

### The Golden Metrics

| Metric | What It Measures | Example |
|--------|-----------------|---------|
| **Success Rate** | % of non-5xx responses | 99.2% of requests to store-api succeed |
| **Request Rate** | Requests per second | order-service receives 150 req/s |
| **Latency** | Response time distribution (p50, p95, p99) | store-api p99 = 45ms |
| **TCP Connections** | Active connections between services | 12 connections to postgres |

```
These metrics are collected by the PROXY — not your application.
No code changes, no Prometheus instrumentation, no logging.
The proxy sees every request and response.
```

### Metrics Flow

```
┌────────────┐     ┌────────────┐     ┌────────────┐     ┌──────────┐
│ linkerd-    │────►│ Prometheus │────►│  Grafana   │────►│ You (UI) │
│ proxy       │     │ (scrapes   │     │ dashboards │     │          │
│ (per pod)   │     │  /metrics) │     │            │     │          │
└────────────┘     └────────────┘     └────────────┘     └──────────┘

Installed via: linkerd viz install | kubectl apply -f -
```

### Pre-Built Dashboards

```bash
linkerd viz dashboard
# Opens browser with:
#
# TOP-LEVEL VIEW:
#   Namespace: order-system
#   ├── store-api        SR: 99.8%  RPS: 45   P50: 12ms  P99: 89ms
#   ├── order-service    SR: 99.2%  RPS: 30   P50: 25ms  P99: 150ms
#   └── inventory-worker SR: 100%   RPS: 30   P50: 5ms   P99: 15ms
#
# CLICK order-service:
#   Inbound:
#     FROM client (browser) → GET /api/orders     SR:99%  P99:120ms
#     FROM client (browser) → POST /api/orders    SR:98%  P99:250ms
#   Outbound:
#     TO store-api → GET /api/products/{id}        SR:99.5% P99:45ms
#     TO kafka (TCP)                                connections: 3
#     TO redis (TCP)                                connections: 5
#     TO postgres (TCP)                             connections: 10
```

---

## 7. Service Profiles — Per-Route Intelligence

### What Is a ServiceProfile?

A ServiceProfile tells Linkerd about the **HTTP routes** a service exposes.
Without it, Linkerd only sees "requests to store-api". With it, Linkerd sees
"GET /api/products" vs "GET /api/products/{id}" vs "POST /api/products".

```yaml
apiVersion: linkerd.io/v1alpha2
kind: ServiceProfile
metadata:
  name: store-api.order-system.svc.cluster.local    # Must match DNS name
  namespace: order-system
spec:
  routes:
    - name: GET /api/products
      condition:
        method: GET
        pathRegex: /api/products
      responseClasses:
        - condition:
            status:
              min: 500
              max: 599
          isFailure: true
      isRetryable: true                              # Safe to retry GETs
      timeout: 5s                                     # Per-route timeout

    - name: GET /api/products/{id}
      condition:
        method: GET
        pathRegex: /api/products/[^/]+
      isRetryable: true
      timeout: 3s

    - name: POST /api/products
      condition:
        method: POST
        pathRegex: /api/products
      isRetryable: false                             # NOT safe to retry POSTs
      timeout: 10s
```

### Auto-Generating Service Profiles

```bash
# From OpenAPI/Swagger spec (your services have Swagger!)
linkerd profile --open-api http://store-api:5116/swagger/v1/swagger.json \
  store-api -n order-system > store-api-profile.yaml

# From live traffic (tap for 60 seconds)
linkerd viz profile --tap deployment/store-api -n order-system --tap-duration 60s \
  store-api > store-api-profile.yaml
```

### Per-Route Metrics (After ServiceProfile)

```bash
linkerd viz routes deployment/order-service -n order-system --to service/store-api

# ROUTE                        SUCCESS   RPS   LATENCY_P50  LATENCY_P95  LATENCY_P99
# GET /api/products             100.00%  15.2  8ms          25ms         45ms
# GET /api/products/{id}         99.50%  12.8  10ms         40ms         120ms
# POST /api/products              0.00%   0.0  0ms          0ms          0ms
# [DEFAULT]                      98.00%   2.1  15ms         50ms         200ms
```

---

## 8. Traffic Management — Retries, Timeouts, Splits

### Retries

```yaml
# In ServiceProfile
spec:
  routes:
    - name: GET /api/products/{id}
      isRetryable: true         # Linkerd will retry on failure

# Configure retry budget (default: 20% extra traffic)
# If 100 RPS, Linkerd allows up to 20 retries/second
```

```
Request flow with retries:

  OrderService → proxy → StoreApi
                           ↓
                   502 Bad Gateway
                           ↓
                 proxy retries (attempt 2)
                           ↓
                       StoreApi
                           ↓
                    200 OK ✓

OrderService sees: 200 OK (it doesn't know there was a retry)
Metrics show: the retry in the retry count
```

### Timeouts

```yaml
spec:
  routes:
    - name: GET /api/products
      timeout: 5s              # If StoreApi doesn't respond in 5s → 504
```

```
Without timeout:
  OrderService → StoreApi (hangs for 30 seconds) → timeout from Kestrel

With Linkerd timeout:
  OrderService → proxy → StoreApi (hangs...)
                  ↓
          5 seconds later
                  ↓
          proxy returns 504 Gateway Timeout
          StoreApi request is cancelled
```

### Traffic Splits (Canary Deployments)

```yaml
apiVersion: split.smi-spec.io/v1alpha2
kind: TrafficSplit
metadata:
  name: store-api-canary
  namespace: order-system
spec:
  service: store-api
  backends:
    - service: store-api-stable
      weight: 900                    # 90% of traffic
    - service: store-api-canary
      weight: 100                    # 10% of traffic
```

```
                    ┌──────── 90% ─────► store-api-stable (v1.0)
OrderService ──►   │
                    └──────── 10% ─────► store-api-canary (v2.0)

Monitor canary metrics in Linkerd dashboard.
If canary is healthy → shift to 100%.
If canary has errors → shift to 0% (rollback).
```

---

## 9. Multi-Cluster & Service Mirroring

```
┌──────────────────┐        ┌──────────────────┐
│  Cluster A (West) │        │  Cluster B (East) │
│                    │        │                    │
│  order-service     │◄──────►│  order-service     │
│  store-api         │ mirror │  store-api         │
│  inventory-worker  │        │  inventory-worker  │
│                    │        │                    │
│  Linkerd control   │        │  Linkerd control   │
│  plane             │        │  plane             │
└──────────────────┘        └──────────────────┘

Services in Cluster A can call services in Cluster B
as if they were local: store-api-east.order-system.svc.cluster.local
Full mTLS across clusters.
```

---

## 10. Authorization Policies

### Default Behavior

By default, Linkerd allows all traffic between meshed services. You can add policies
to restrict which services can call which:

```yaml
# Only order-service can call store-api
apiVersion: policy.linkerd.io/v1beta3
kind: Server
metadata:
  name: store-api-http
  namespace: order-system
spec:
  podSelector:
    matchLabels:
      app: store-api
  port: 5116
  proxyProtocol: HTTP/1
---
apiVersion: policy.linkerd.io/v1beta3
kind: AuthorizationPolicy
metadata:
  name: store-api-allow-order-service
  namespace: order-system
spec:
  targetRef:
    group: policy.linkerd.io
    kind: Server
    name: store-api-http
  requiredAuthenticationRefs:
    - name: order-service
      kind: MeshTLSAuthentication
      group: policy.linkerd.io
---
apiVersion: policy.linkerd.io/v1beta1
kind: MeshTLSAuthentication
metadata:
  name: order-service
  namespace: order-system
spec:
  identities:
    - "order-service.order-system.serviceaccount.identity.linkerd.cluster.local"
```

```
After applying:
  order-service → store-api     ✓ Allowed (identity matches policy)
  inventory-worker → store-api  ✗ Denied (identity not in policy)
  random-pod → store-api        ✗ Denied
```

---

## 11. Extensions — Viz, Jaeger, Multicluster

| Extension | What It Adds | Install Command |
|-----------|-------------|-----------------|
| **viz** | Prometheus + Grafana + dashboard + tap/top/stat | `linkerd viz install \| kubectl apply -f -` |
| **jaeger** | Distributed tracing (OpenTelemetry) | `linkerd jaeger install \| kubectl apply -f -` |
| **multicluster** | Cross-cluster service mirroring | `linkerd multicluster install \| kubectl apply -f -` |

### Viz — Essential Commands

```bash
# Real-time request table (like `top` for network)
linkerd viz top deployment/order-service -n order-system
# Shows live request/response pairs with method, path, status, latency

# Tap — stream live requests
linkerd viz tap deployment/order-service -n order-system
# req id=0:1 proxy=in  src=10.0.0.5:52341 dst=10.0.0.8:5200 :method=POST :path=/api/orders
# rsp id=0:1 proxy=in  src=10.0.0.5:52341 dst=10.0.0.8:5200 :status=201 latency=45ms

# Stat — summary statistics
linkerd viz stat deployment -n order-system
# NAME                MESHED   SUCCESS   RPS   LATENCY_P50  LATENCY_P95  LATENCY_P99
# store-api           1/1      99.80%    45.2  12ms         25ms         89ms
# order-service       1/1      99.20%    30.1  25ms         80ms         150ms
# inventory-worker    1/1      100.00%   30.0  5ms          10ms         15ms
```

---

## 12. How Linkerd Integrates with Your Order System

### Your Communication Paths

```
┌─────────────────────────────────────────────────────────────────┐
│                YOUR ORDER SYSTEM WITH LINKERD                    │
│                                                                  │
│  ┌──────────┐  HTTP/mTLS   ┌──────────────┐   Kafka/TCP        │
│  │ StoreApi  │◄────────────│ OrderService  │──────────────┐     │
│  │           │  Linkerd     │              │                │     │
│  │           │  retries,    │              │  Redis/TCP     │     │
│  │           │  metrics,    │              │──────────┐     │     │
│  │           │  timeout     │              │          │     │     │
│  └─────┬────┘              └──────┬───────┘          │     │     │
│        │                          │                   │     │     │
│  ┌─────┴────┐              ┌──────┴───────┐   ┌──────┴──┐  │     │
│  │ SQL Svr  │              │  PostgreSQL  │   │  Redis  │  │     │
│  │ (not     │              │  (not meshed │   │  (not   │  │     │
│  │ meshed)  │              │   — TCP only)│   │ meshed) │  │     │
│  └──────────┘              └─────────────┘   └─────────┘  │     │
│                                                             │     │
│                                                      ┌──────┴──┐ │
│                                                      │  Kafka  │ │
│  ┌─────────────────┐  Kafka/TCP                      │  (not   │ │
│  │InventoryWorker  │────────────────────────────────►│ meshed) │ │
│  │ (meshed)        │                                  └─────────┘ │
│  └─────────────────┘                                              │
│                                                                    │
│  WHAT LINKERD GIVES YOU:                                          │
│  ═══════════════════════                                          │
│  ✓ OrderService → StoreApi: mTLS, per-route metrics, retries     │
│  ✓ TCP connections to Kafka/Redis/DBs: mTLS (one-sided if not    │
│    meshed), connection-level metrics                              │
│  ✓ All .NET services: golden metrics (success, latency, RPS)     │
│  ✗ Kafka message processing: NOT visible (happens inside pod)    │
│  ✗ Database queries: NOT visible (only TCP connection metrics)   │
└─────────────────────────────────────────────────────────────────┘
```

### What You Can Remove From Your Code

```csharp
// BEFORE (OrderService/Program.cs — you have this today):
var retryPolicy = HttpPolicyExtensions
    .HandleTransientHttpError()
    .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

builder.Services.AddHttpClient<StoreApiClient>(client => { ... })
    .AddPolicyHandler(retryPolicy);   // ← Polly retry

// AFTER (with Linkerd ServiceProfile retries):
// You CAN keep Polly — it's not harmful. But Linkerd also retries.
// Choose one to avoid double-retrying:
//   Option A: Keep Polly, disable Linkerd retries (isRetryable: false)
//   Option B: Remove Polly, use Linkerd retries (isRetryable: true)
//
// Option B is cleaner — retries are infrastructure, not business logic.
// But Option A is safer if you want retry control in your code.
```

---

## 13. Debugging with Linkerd

### "Why Are Requests Failing?"

```bash
# Step 1: Check overall health
linkerd viz stat deployment -n order-system
# Look for low success rates

# Step 2: Which route is failing?
linkerd viz routes deployment/order-service --to service/store-api -n order-system
# Shows per-route success rates

# Step 3: See actual failing requests
linkerd viz tap deployment/order-service -n order-system --to deployment/store-api
# Live stream of requests — look for non-2xx status codes

# Step 4: Check if mTLS is working
linkerd viz edges deployment -n order-system
# If TLS column shows false → sidecar might not be injected
```

### "Is Linkerd Causing My Problem?"

```bash
# Check proxy health
linkerd check --proxy -n order-system

# Check proxy logs for errors
kubectl logs deployment/order-service -c linkerd-proxy -n order-system

# Check proxy metrics directly
kubectl port-forward deployment/order-service 4191:4191 -n order-system
curl http://localhost:4191/metrics | grep -E "request_total|response_total"
```

---

## 14. Performance Impact

### Overhead Added by Linkerd

| Metric | Without Linkerd | With Linkerd | Difference |
|--------|----------------|-------------|------------|
| **Latency (p50)** | 10ms | 10.5ms | +0.5ms |
| **Latency (p99)** | 50ms | 51ms | +1ms |
| **Memory per pod** | Varies | +20-30MB | Proxy memory |
| **CPU per pod** | Varies | +10-50m | Proxy CPU |
| **Pod startup time** | 2s | 3s | Init container |

**The overhead is negligible** for almost all workloads. The observability and
security gains far outweigh the cost.

---

## 15. Common Mistakes & Gotchas

| Mistake | Symptom | Fix |
|---------|---------|-----|
| Forgetting `linkerd.io/inject` annotation | 1/1 pods (no sidecar) | Add annotation, restart pods |
| Not installing viz extension | `linkerd viz` commands fail | `linkerd viz install \| kubectl apply -f -` |
| ServiceProfile name doesn't match DNS | No per-route metrics | Use `<svc>.<ns>.svc.cluster.local` |
| Mixing Polly retries + Linkerd retries | Double retries, amplified load | Choose one |
| Injecting into infra pods (Redis/Kafka) | Unexpected behavior | Only inject into your services |
| Not checking `linkerd check` before deploying | Cert issues, proxy failures | Always run `linkerd check` |
| Skip preinstall checks | Missing CRDs, incompatible K8s | Run `linkerd check --pre` first |

---

## 16. Command Reference

```bash
# ── INSTALL & CHECK ────────────────────────
linkerd check --pre                          # Pre-install checks
linkerd install --crds | kubectl apply -f -  # Install CRDs
linkerd install | kubectl apply -f -         # Install control plane
linkerd check                                # Verify installation
linkerd viz install | kubectl apply -f -     # Install observability
linkerd viz check                            # Verify viz

# ── INJECTION ──────────────────────────────
linkerd inject deployment.yaml | kubectl apply -f -   # Inject via CLI
kubectl annotate ns order-system linkerd.io/inject=enabled  # Inject namespace

# ── OBSERVABILITY ──────────────────────────
linkerd viz dashboard                        # Open web dashboard
linkerd viz stat deployment -n order-system  # Summary stats
linkerd viz top deployment/order-service     # Real-time request table
linkerd viz tap deployment/order-service     # Stream live requests
linkerd viz routes deployment/order-service --to svc/store-api  # Per-route stats
linkerd viz edges deployment -n order-system # Show mTLS connections

# ── DEBUGGING ──────────────────────────────
linkerd check --proxy -n order-system        # Check proxy health
linkerd diagnostics proxy-metrics -n order-system deployment/order-service  # Raw metrics
linkerd identity -n order-system             # Check certificates

# ── PROFILES ───────────────────────────────
linkerd profile --open-api swagger.json svc-name  # Generate from Swagger
linkerd viz profile --tap deployment/svc svc-name # Generate from traffic

# ── UNINSTALL ──────────────────────────────
linkerd viz uninstall | kubectl delete -f -  # Remove viz
linkerd uninstall | kubectl delete -f -      # Remove control plane
```
