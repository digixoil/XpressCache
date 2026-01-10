# XpressCache - Known Issues & Roadmap

This document tracks known issues, limitations, and planned enhancements for XpressCache.

## Known Issues

### 🔴 High Priority

#### ~~Custom Validation with Cached Items~~ ✅ FIXED in v1.0.1
**Status:** ✅ Resolved  
**Affected Version:** 1.0.0.1  
**Fixed in:** v1.0.1  

**Issue:** When `customValidate` was provided, the fast-path cache hit was bypassed, forcing the async validation path which acquired locks unnecessarily and called recovery even for cache hits.

**Resolution:**
- Added synchronous validation parameter (`syncValidate`) alongside async (`asyncValidate`)
- Synchronous validation executes in the lock-free fast path
- Async validation only executes when item is found in cache
- Both validators can be used together (sync runs first, then async)
- Recovery is only called when validation actually fails

**Migration Guide:**
```csharp
// Before (v1.0.0.1) - forced async path, called recovery unnecessarily
var item = await cache.LoadItem<T>(
    id, subject, recovery,
    customValidate: async (x) => await ValidateAsync(x)
);

// After (v1.0.1+) - use sync validation for best performance
var item = await cache.LoadItem<T>(
    id, subject, recovery,
    syncValidate: (x) => x.IsValid  // Fast path!
);

// Or use async when needed
var item = await cache.LoadItem<T>(
    id, subject, recovery,
    asyncValidate: async (x) => await ValidateAsync(x)
);

// Or combine both
var item = await cache.LoadItem<T>(
    id, subject, recovery,
    syncValidate: (x) => x.IsValid,        // Checked first (fast)
    asyncValidate: async (x) => await DbCheck(x)  // Then async if needed
);
```

---

### 🟡 Medium Priority

#### ~~Nullable Reference Types Not Enabled~~ ✅ COMPLETED
**Status:** ✅ Resolved  
**Affected Version:** 1.0.0.1  
**Fixed in:** v1.0.1

**Issue:** Project did not have nullable reference types enabled at project level.

**Resolution:**
- Enabled `<Nullable>enable</Nullable>` in project file
- Updated all public APIs with proper null annotations
- Added nullable annotations to internal types
- All method parameters and return types properly annotated

---

#### ~~Performance Tests May Interfere with Unit Tests~~ ✅ COMPLETED
**Status:** ✅ Resolved  
**Affected Version:** 1.0.0.1  
**Fixed in:** v1.0.1

**Issue:** Performance/benchmark tests ran in the same test suite as unit tests, potentially causing resource contention and timeouts in CI environments.

**Resolution:**
- Added trait-based test categorization
- Tests tagged with `[Trait("Category", "Performance")]` and `[Trait("Category", "Benchmark")]`
- Speed traits added: `Fast`, `Medium`, `Slow`, `Stress`
- Created comprehensive Testing Guide documentation

**Usage:**
```bash
# Run only unit tests (exclude performance tests)
dotnet test --filter "Category!=Performance&Category!=Benchmark"

# Run only fast tests
dotnet test --filter "Speed=Fast"

# Run only performance tests
dotnet test --filter "Category=Performance"
```

See [Testing-Guide.md](Testing-Guide.md) for complete documentation.

---

### 🟢 Low Priority

#### No Eviction Policy Beyond TTL
**Status:** 💡 Future Enhancement  
**Issue:** Cache only supports time-based eviction, no LRU/LFU or size-based eviction.

**Impact:**
- Memory can grow until TTL expiration
- No automatic eviction of rarely-used items

**Mitigation:**
- Use appropriate `DefaultTtlMs` setting
- Configure `ProbabilisticCleanupThreshold`
- Manual cleanup via `CleanupCache()`

**Planned:** v2.0.0 (breaking change potential)

---

#### GetCachedItems Performance
**Status:** ℹ️ By Design  
**Issue:** `GetCachedItems<T>` is O(n) operation requiring full cache scan.

**Impact:**
- Slow for large caches
- Not suitable for hot paths

**Mitigation:**
- Use sparingly
- Consider secondary index if frequent access needed

**Note:** This is by design for simplicity. Future versions may offer indexed access.

---

## Roadmap

### ~~Version 1.0.1 (Patch)~~ ✅ RELEASED
**Focus:** Bug Fixes & Test Improvements

- [x] Fix custom validation performance issue
- [x] Add synchronous validation overload
- [x] Enable nullable reference types
- [x] Update all APIs with proper null annotations
- [x] Separate performance tests with trait categorization
- [x] Add comprehensive testing guide
- [x] Improve test reliability
- [x] Documentation improvements

**Released:** [Date]

---

### Version 1.1.0 (Minor - Planned)
**Focus:** Developer Experience & Observability

#### Developer Experience
- [ ] Add Source Link support for debugging
- [ ] Improve exception messages with more context
- [ ] Add telemetry/metrics hooks (optional)
- [ ] Enhanced logging with structured data
- [ ] Add validation performance benchmarks

#### Code Quality
- [ ] Add code coverage reporting
- [ ] Performance regression detection
- [ ] Memory leak detection tests

**Est. Release:** 1-2 months after 1.0.1

---

### Version 1.2.0 (Minor - Planned)
**Focus:** Performance & Monitoring

#### Performance Enhancements
- [ ] Investigate memory pooling for cache entries
- [ ] Optimize hash code computation
- [ ] Reduce allocation in hot paths
- [ ] Benchmark against competitors (IMemoryCache, etc.)

#### Monitoring & Observability
- [ ] Add cache statistics (hit rate, miss rate, etc.)
- [ ] Provide metrics interface for monitoring tools
- [ ] Add health check support
- [ ] Performance counters for diagnostics

#### Additional Features
- [ ] Configurable cleanup strategies
- [ ] Bulk operations (SetMany, RemoveMany)
- [ ] Cache entry change notifications (optional)
- [ ] Separate benchmark project using BenchmarkDotNet

**Est. Release:** 3-4 months after 1.0.1

---

### Version 2.0.0 (Major - Future)
**Focus:** Advanced Features

#### Eviction Policies
- [ ] LRU (Least Recently Used) eviction
- [ ] LFU (Least Frequently Used) eviction
- [ ] Size-based eviction with memory limits
- [ ] Pluggable eviction strategy

#### Advanced Caching
- [ ] Cache partitioning for better concurrency
- [ ] Priority-based caching
- [ ] Dependency tracking between cache entries
- [ ] Conditional updates (CAS semantics)

#### Breaking Changes
- [ ] Revise API based on 1.x feedback
- [ ] Remove deprecated features
- [ ] Optimize data structures based on production usage

**Est. Release:** 12+ months after 1.0.0.1

---

## Changelog

### v1.0.1 (Current)

#### Fixed
- **Custom Validation Bug**: Synchronous validation now executes in fast path without acquiring locks
- **Nullability**: All APIs properly annotated with nullable reference types
- **Performance Tests**: Separated with trait-based categorization

#### Added
- `syncValidate` parameter for synchronous validation (preferred for performance)
- `asyncValidate` parameter for async validation (when needed)
- Test traits: `Category`, `Speed` for better test organization
- Comprehensive testing guide documentation

#### Changed
- **BREAKING**: `LoadItem<T>` signature changed - `customValidate` parameter renamed and split:
  - Old: `Func<T, Task<bool>> customValidate`
  - New: `Func<T, bool>? syncValidate` and `Func<T, Task<bool>>? asyncValidate`
- Nullable reference types enabled project-wide
- Improved inline documentation with nullability context

#### Migration
```csharp
// Old code (v1.0.0.1)
await cache.LoadItem(id, subject, recovery, 
    customValidate: async x => x.IsValid);

// New code (v1.0.1) - prefer sync for performance
await cache.LoadItem(id, subject, recovery, 
    syncValidate: x => x.IsValid);

// Or use async when needed
await cache.LoadItem(id, subject, recovery, 
    asyncValidate: async x => await CheckAsync(x));
```

### v1.0.0.1 (Initial Release)

- Initial implementation with cache stampede prevention
- Multi-targeting: .NET 6.0, 7.0, 8.0
- Thread-safe operations with per-key locking
- Sliding expiration with TTL
- Probabilistic cleanup

---

## How to Report Issues

### Bug Reports

Please include:
1. XpressCache version
2. .NET version and runtime
3. Minimal reproduction code
4. Expected vs actual behavior
5. Stack trace if applicable

**Submit to:** GitHub Issues

### Feature Requests

Please include:
1. Use case description
2. Proposed API or behavior
3. Alternatives considered
4. Impact on existing features

**Submit to:** GitHub Discussions

---

## Contributing

We welcome contributions! Areas where help is especially appreciated:

### High Impact
- 🐛 Bug fixes for known issues
- 📝 Documentation improvements
- ✅ Additional test coverage
- 🔬 Performance benchmarks

### Medium Impact
- 💡 Feature implementations from roadmap
- 🎨 Code quality improvements
- 📊 Monitoring/metrics features

### How to Contribute
1. Check existing issues/PRs to avoid duplication
2. For major features, open a discussion first
3. Follow existing code style and patterns
4. Include tests for new functionality
5. Update documentation as needed

See [CONTRIBUTING.md](CONTRIBUTING.md) for detailed guidelines.

---

## Version Support Policy

| Version | Support Level | End of Support |
|---------|--------------|----------------|
| 1.0.x   | Security Only | 1.1.0 release |
| 1.1.x   | Full Support | Until 2.0.0 release |
| 1.2.x   | Planned      | TBD |
| 2.0.x   | Planned      | TBD |

**Full Support includes:**
- Bug fixes
- Security updates
- Critical performance issues
- Documentation updates

---

## Legend

| Symbol | Meaning |
|--------|---------|
| 🔴 | High Priority |
| 🟡 | Medium Priority |
| 🟢 | Low Priority |
| ⚠️ | Under Investigation |
| 📋 | Planned |
| 💡 | Future Enhancement |
| ℹ️ | By Design |
| ✅ | Completed/Resolved |
| ⏳ | In Progress |

---

Last Updated: 2024
