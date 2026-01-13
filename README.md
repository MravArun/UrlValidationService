# URL Validation Service

A scalable .NET Web API that validates URLs with **hybrid sync/async processing** - perfect for interview discussions on system design and scalability.

---

## 🎯 API Endpoints

### 1. Add Links
```
POST /api/links
```
Store URLs in MongoDB for validation.

**Request:**
```json
{
  "urls": [
    "https://google.com",
    "https://github.com",
    "https://invalid-domain-xyz.com"
  ]
}
```

**Response (201 Created):**
```json
{
  "addedCount": 3,
  "message": "Successfully stored 3 links. Call POST /api/links/validate to check them."
}
```

---

### 2. Trigger Validation (Hybrid Sync/Async)
```
POST /api/links/validate
```

**Behavior:**
- **≤ 10 links (threshold):** Synchronous - immediate results
- **> 10 links:** Asynchronous - returns jobId for polling

**Sync Response (200 OK):**
```json
{
  "isComplete": true,
  "totalLinks": 5,
  "validatedCount": 5,
  "brokenCount": 1,
  "message": "Validated 5 links synchronously. Found 1 broken."
}
```

**Async Response (202 Accepted):**
```json
{
  "isComplete": false,
  "jobId": "abc123-def456",
  "totalLinks": 1000000,
  "validatedCount": 0,
  "brokenCount": 0,
  "message": "Processing 1000000 links asynchronously. Poll GET /api/links/jobs/abc123-def456 for status."
}
```

---

### 3. Poll Job Status (Async)
```
GET /api/links/jobs/{jobId}
```

**Response (200 OK):**
```json
{
  "isComplete": false,
  "jobId": "abc123-def456",
  "totalLinks": 1000000,
  "validatedCount": 250000,
  "brokenCount": 1523,
  "message": "Processing: 250000/1000000 (25%)"
}
```

---

### 4. Get Broken Links
```
GET /api/links/broken
```

**Response (200 OK):**
```json
{
  "totalBroken": 2,
  "links": [
    {
      "url": "https://broken.com",
      "httpStatusCode": 404,
      "failureReason": "404 Not Found",
      "responseTimeMs": 150,
      "lastValidatedAt": "2025-01-13T10:30:00Z",
      "createdAt": "2025-01-13T10:25:00Z"
    },
    {
      "url": "https://timeout.com",
      "httpStatusCode": null,
      "failureReason": "Request timed out after 10s",
      "responseTimeMs": 10000,
      "lastValidatedAt": "2025-01-13T10:30:00Z",
      "createdAt": "2025-01-13T10:25:00Z"
    }
  ]
}
```

---

## 🏗 Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        API Layer                                 │
│  POST /api/links           - Store links                         │
│  POST /api/links/validate  - Trigger validation                  │
│  GET  /api/links/jobs/{id} - Poll job status                     │
│  GET  /api/links/broken    - Get broken links                    │
└───────────────────────────┬─────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│                  LinkValidationService                           │
│  Decides sync vs async based on link count threshold             │
└───────────┬─────────────────────────────────┬───────────────────┘
            │                                 │
            │ ≤ threshold                     │ > threshold
            ▼                                 ▼
┌───────────────────────┐        ┌────────────────────────────────┐
│  Sync Processing      │        │  Async Job Creation            │
│  - Immediate results  │        │  - Returns jobId               │
│  - No retries         │        │  - Background processing       │
└───────────┬───────────┘        └──────────────┬─────────────────┘
            │                                   │
            │                                   ▼
            │                    ┌────────────────────────────────┐
            │                    │  ValidationWorker (Background) │
            │                    │  - Polls for queued jobs       │
            │                    │  - Batch processing            │
            │                    │  - Progress updates            │
            │                    │  - Atomic job claiming         │
            │                    └──────────────┬─────────────────┘
            │                                   │
            └───────────────────┬───────────────┘
                                │
                                ▼
            ┌───────────────────────────────────────────┐
            │           Shared Validation Logic          │
            │  - URL normalization                       │
            │  - HTTP HEAD request                       │
            │  - Cache-aside pattern                     │
            │  - Rate limiting per host                  │
            │  - Circuit breaker                         │
            └───────────────────────────────────────────┘
```

---

## 🔑 Key Design Decisions

### 1. Hybrid Sync/Async Processing
**Trade-off:** User experience vs scalability

| Scenario | Approach | Why |
|----------|----------|-----|
| ≤10 links | Sync | Fast response, no polling needed |
| >10 links | Async job | Prevents timeout, enables progress tracking |
| Millions | Background worker | Scalable, resumable, observable |

### 2. Atomic Job Claiming
**Interview Talking Point:** Multiple workers can scale horizontally without coordination.

```csharp
// Uses MongoDB findAndModify - atomic claim prevents duplicate processing
var job = await _collection.FindOneAndUpdateAsync(
    filter: j => j.Status == JobStatus.Queued,
    update: Builders<ValidationJob>.Update.Set(j => j.Status, JobStatus.Processing)
);
```

### 3. Batch Processing with Progress
- Worker processes links in configurable batches (default: 50)
- Updates job progress after each batch
- Enables real-time progress via polling

### 4. Resilience Patterns
- **Circuit Breaker:** Fails fast after consecutive failures
- **Timeout:** Bounded wait per request
- **Rate Limiting:** Per-host throttling to avoid bans
- **Caching:** Avoids repeated validations

---

## 📁 Project Structure

```
/Controllers
    LinksController.cs          # REST endpoints
/Services
    LinkValidationService.cs    # Core logic + sync/async decision
/Background
    ValidationWorker.cs         # Background job processor
/Repositories
    LinkRepository.cs           # Link persistence
    JobRepository.cs            # Job persistence + atomic claiming
/Models
    Link.cs                     # Link entity
    ValidationJob.cs            # Job entity
    Dtos.cs                     # Request/response DTOs
/Abstractions
    ILinkRepository.cs
    IJobRepository.cs
    IValidationCache.cs
/Infrastructure
    InMemoryValidationCache.cs
    HttpClientConfiguration.cs  # Polly policies
    RateLimiter.cs
```

---

## 🚀 Quick Start

### Prerequisites
- .NET 8 SDK
- MongoDB (local or Docker)

### Run MongoDB (Docker)
```bash
docker run -d -p 27017:27017 --name mongodb mongo:7
```

### Run the API
```bash
dotnet run
```

### Test Flow
```bash
# 1. Add links (small batch - sync)
curl -X POST http://localhost:5000/api/links \
  -H "Content-Type: application/json" \
  -d '{"urls": ["https://google.com", "https://github.com"]}'

# 2. Validate (sync - immediate results)
curl -X POST http://localhost:5000/api/links/validate

# 3. Add many links (large batch - async)
curl -X POST http://localhost:5000/api/links \
  -H "Content-Type: application/json" \
  -d '{"urls": ["https://url1.com", "https://url2.com", ...(100+ URLs)...]}'

# 4. Validate (async - returns jobId)
curl -X POST http://localhost:5000/api/links/validate

# 5. Poll job status
curl http://localhost:5000/api/links/jobs/{jobId}

# 6. Get broken links
curl http://localhost:5000/api/links/broken
```

Open **http://localhost:5000** for Swagger UI.

---

## 🎯 Interview Talking Points

### What I Included
1. **Hybrid sync/async** - Optimal UX for small batches, scalable for large
2. **Background worker** - Handles millions without blocking
3. **Atomic job claiming** - Safe horizontal scaling
4. **Progress tracking** - Real-time job status
5. **Resilience patterns** - Circuit breaker, timeout, rate limiting
6. **Caching** - Avoids redundant validations

### What I Intentionally Skipped (but can discuss)
1. **Message queues** - Would use RabbitMQ/Azure Service Bus for extreme scale
2. **Distributed cache** - Redis for multi-instance deployments
3. **Retry with dead-letter** - Failed URLs go to DLQ for manual review
4. **Partitioned processing** - Split by domain for better rate limiting

### Scalability Path
```
Small       →   Medium        →   Large           →   Extreme
Sync        →   Background    →   Multiple        →   Message Queue
Processing      Worker            Workers             + Partitioning
```

---

## 📊 Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `Validation:SyncThreshold` | 10 | Links above this → async job |
| `Validation:RequestTimeoutSeconds` | 10 | Per-URL HTTP timeout |
| `Validation:MaxConcurrency` | 20 | Parallel HTTP requests |
| `Validation:BatchSize` | 50 | URLs per worker batch |
| `Validation:WorkerPollingIntervalSeconds` | 5 | Job poll frequency |
| `Cache:TtlMinutes` | 60 | Cache entry lifetime |

Override via environment:
```bash
Validation__SyncThreshold=100 dotnet run
```

---

## 🧪 Running Tests

```bash
dotnet test
```

---

## 📝 License

Interview demo - use freely.
