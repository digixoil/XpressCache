# XpressCache - Known Issues & Roadmap

This document tracks known issues, limitations, and planned enhancements for XpressCache.

## Known Issues

### ?? High Priority

#### Custom Validation with Cached Items
**Status:** ?? Under Investigation  
**Affected Version:** 1.0.0  
**Issue:** When `customValidate` is provided, the fast-path cache hit is bypassed, forcing the async validation path which may acquire locks unnecessarily.

**Impact:**
- Performance degradation when using custom validation on cache hits
- Potential unnecessary lock acquisition

**Workaround:**
- Avoid custom validation for high-frequency cache hits
- Use custom validation only when data freshness is critical

**Planned Fix:** v1.1.0
- Implement synchronous validation path for simple validators
- Add `Func<T, bool>` overload for sync validation alongside async version
- Optimize double-check to avoid lock when validation succeeds

**Tracking:** See `doc/Known-Issues.md` and test `LoadItem_CustomValidateReturnsTrue_UsesCached`

---

### ?? Medium Priority

#### Nullable Reference Types Not Enabled
**Status:** ?? Planned  
**Affected Version:** 1.0.0  
**Issue:** Project does not have nullable reference types enabled at project level.

**Impact:**
- Potential null reference warnings/errors when enabled
- Less compile-time safety for null checks

**Planned Fix:** v1.1.0
- Enable `<Nullable>enable</Nullable>` in project file
- Audit and fix all null-related code paths
- Add proper null annotations to public API

---

#### Performance Tests May Interfere with Unit Tests
**Status:** ?? Planned  
**Affected Version:** 1.0.0  
**Issue:** Performance/benchmark tests run in the same test suite as unit tests, potentially causing resource contention and timeouts in CI environments.

**Impact:**
- Occasional test failures in slow environments
- Longer test execution times
- Resource contention on CI servers

**Workaround:**
- Run tests with `--filter` to exclude benchmark tests:
  ```bash
  dotnet test --filter "FullyQualifiedName!~Benchmark"
  ```

**Planned Fix:** v1.0.1
- Separate benchmark tests into dedicated test project
- Add test categories/traits for filtering
- Create separate CI pipeline stage for performance tests

---

### ?? Low Priority

#### No Eviction Policy Beyond TTL
**Status:** ?? Future Enhancement  
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
**Status:** ?? By Design  
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

### Version 1.0.1 (Patch - Q2 2024)
**Focus:** Bug Fixes & Test Improvements

- [ ] Fix custom validation performance issue
- [ ] Separate performance tests into dedicated project
- [ ] Add test categories for filtering
- [ ] Improve test reliability in CI environments
- [ ] Documentation improvements

**Est. Release:** Within 2-4 weeks of 1.0.0 release

---

### Version 1.1.0 (Minor - Q3 2024)
**Focus:** Code Quality & Developer Experience

#### Nullable Reference Types
- [ ] Enable nullable reference types project-wide
- [ ] Audit all code paths for null safety
- [ ] Add proper null annotations to public API
- [ ] Update documentation

#### Custom Validation Improvements
- [ ] Add synchronous validation overload (`Func<T, bool>`)
- [ ] Optimize validation path to avoid unnecessary locking
- [ ] Add validation performance benchmarks
- [ ] Document validation best practices

#### Developer Experience
- [ ] Add Source Link support for debugging
- [ ] Improve exception messages
- [ ] Add telemetry/metrics hooks (optional)
- [ ] Enhanced logging with structured data

**Est. Release:** 2-3 months after 1.0.0

---

### Version 1.2.0 (Minor - Q4 2024)
**Focus:** Performance & Monitoring

#### Performance Enhancements
- [ ] Investigate memory pooling for cache entries
- [ ] Optimize hash code computation
- [ ] Reduce allocation in hot paths
- [ ] Benchmark against competitors

#### Monitoring & Observability
- [ ] Add cache statistics (hit rate, miss rate, etc.)
- [ ] Provide metrics interface for monitoring tools
- [ ] Add health check support
- [ ] Performance counters for diagnostics

#### Additional Features
- [ ] Configurable cleanup strategies
- [ ] Bulk operations (SetMany, RemoveMany)
- [ ] Cache entry change notifications (optional)

**Est. Release:** 4-6 months after 1.0.0

---

### Version 2.0.0 (Major - 2025)
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

**Est. Release:** 12+ months after 1.0.0

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
- ?? Bug fixes for known issues
- ?? Documentation improvements
- ? Additional test coverage
- ?? Performance benchmarks

### Medium Impact
- ?? Feature implementations from roadmap
- ?? Code quality improvements
- ?? Monitoring/metrics features

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
| 1.0.x   | Full Support | Until 2.0.0 release |
| 1.1.x   | Full Support | Until 2.0.0 release |
| 1.2.x   | Full Support | Until 2.0.0 release |
| 2.0.x   | Planned      | TBD |

**Full Support includes:**
- Bug fixes
- Security updates
- Critical performance issues
- Documentation updates

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for detailed version history.

---

## Legend

| Symbol | Meaning |
|--------|---------|
| ?? | High Priority |
| ?? | Medium Priority |
| ?? | Low Priority |
| ?? | Under Investigation |
| ?? | Planned |
| ?? | Future Enhancement |
| ?? | By Design |
| ? | Completed |
| ? | In Progress |

---

Last Updated: 2024
