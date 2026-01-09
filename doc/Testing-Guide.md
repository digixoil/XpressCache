# XpressCache Testing Guide

This guide explains how to run different types of tests in the XpressCache project.

## Test Organization

Tests are organized into categories using xUnit traits:

### Test Categories

| Category | Description | Typical Duration |
|----------|-------------|------------------|
| Unit Tests | Fast, isolated tests | < 100ms per test |
| Performance | Benchmark and throughput tests | 100ms - 5s per test |
| Benchmark | Detailed performance measurements | 1s - 10s per test |
| Stress | High-load concurrent tests | 5s - 30s per test |

### Test Speed Traits

| Speed | Description |
|-------|-------------|
| Fast | < 100ms |
| Medium | 100ms - 1s |
| Slow | > 1s |

---

## Running Tests

### Run All Tests

```bash
dotnet test
```

### Run Only Unit Tests (Exclude Performance/Benchmarks)

```bash
dotnet test --filter "Category!=Performance&Category!=Benchmark"
```

### Run Only Performance Tests

```bash
dotnet test --filter "Category=Performance"
```

### Run Only Benchmark Tests

```bash
dotnet test --filter "Category=Benchmark"
```

### Run Only Stress Tests

```bash
dotnet test --filter "Category=Stress"
```

### Run Fast Tests Only

```bash
dotnet test --filter "Speed=Fast"
```

### Run Tests Excluding Slow Ones

```bash
dotnet test --filter "Speed!=Slow"
```

---

## Combining Filters

### Run Unit Tests (Fast Only)

```bash
dotnet test --filter "Category!=Performance&Category!=Benchmark&Speed=Fast"
```

### Run All Non-Stress Tests

```bash
dotnet test --filter "Category!=Stress"
```

---

## CI/CD Recommendations

### For Pull Requests (Fast Feedback)

```bash
# Run only fast unit tests
dotnet test --filter "Category!=Performance&Category!=Benchmark&Speed!=Slow"
```

**Typical Duration:** 10-30 seconds

### For Main Branch (Comprehensive)

```bash
# Run all unit tests + medium performance tests
dotnet test --filter "Category!=Stress&Speed!=Slow"
```

**Typical Duration:** 1-2 minutes

### Nightly Builds (Full Suite)

```bash
# Run everything including stress tests
dotnet test
```

**Typical Duration:** 3-5 minutes

---

## Viewing Test Output

### With Detailed Output

```bash
dotnet test --logger "console;verbosity=detailed"
```

### With Performance Metrics

```bash
dotnet test --filter "Category=Benchmark" --logger "console;verbosity=normal"
```

This will show benchmark output in the test results.

---

## Test Project Structure

```
XpressCache.Tests/
??? CacheStoreTests.cs           # Basic functionality tests
??? SingleFlightTests.cs         # Stampede prevention tests  
??? ConcurrencyTests.cs          # Thread-safety tests
??? ValidationAndEdgeCaseTests.cs # Edge cases
??? CacheExpiryTests.cs          # TTL and expiration tests
??? AdvancedScenariosTests.cs    # Complex scenarios
??? BenchmarkTests.cs            # Performance benchmarks
    [Trait("Category", "Performance")]
    [Trait("Category", "Benchmark")]
```

---

## IDE Integration

### Visual Studio

1. Open Test Explorer (Test > Test Explorer)
2. Group by Traits
3. Filter by category or speed
4. Right-click category to run

### Visual Studio Code

1. Install C# Dev Kit extension
2. Open Testing view
3. Filter tests using the search box:
   - `@Category:Performance`
   - `@Speed:Fast`

### JetBrains Rider

1. Open Unit Tests window
2. Group by Category
3. Right-click to run specific category

---

## Troubleshooting

### Tests Timing Out

**Symptom:** Tests fail with timeout errors

**Cause:** Running performance tests concurrently with limited resources

**Solution:**
```bash
# Run with single thread
dotnet test --filter "Category=Performance" -- RunConfiguration.MaxCpuCount=1
```

### Inconsistent Performance Results

**Symptom:** Benchmark results vary wildly between runs

**Cause:** Other processes competing for resources

**Solution:**
1. Close unnecessary applications
2. Run benchmarks individually:
   ```bash
   dotnet test --filter "FullyQualifiedName~Benchmark_CacheHit_Performance"
   ```
3. Use BenchmarkDotNet for production benchmarks (future enhancement)

### Out of Memory in Stress Tests

**Symptom:** Tests fail with OutOfMemoryException

**Cause:** Large cache population in stress tests

**Solution:**
```bash
# Skip stress tests
dotnet test --filter "Category!=Stress"
```

---

## Best Practices

### During Development

Run fast tests frequently:
```bash
dotnet test --filter "Speed=Fast"
```

### Before Committing

Run all non-performance tests:
```bash
dotnet test --filter "Category!=Performance&Category!=Benchmark"
```

### Performance Regression Testing

Run benchmark suite and compare results:
```bash
dotnet test --filter "Category=Benchmark" --logger "trx;LogFileName=benchmarks.trx"
```

---

## Future Enhancements

### Planned for v1.0.1

- [ ] Separate benchmark project (`XpressCache.Benchmarks`)
- [ ] Integration with BenchmarkDotNet
- [ ] Automated performance regression detection
- [ ] CI pipeline optimization

### Under Consideration

- [ ] Code coverage reporting
- [ ] Mutation testing
- [ ] Performance baselines tracking
- [ ] Memory leak detection tests

---

## Sample CI Pipeline

### GitHub Actions Example

```yaml
name: Tests

on: [push, pull_request]

jobs:
  unit-tests:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '8.0.x'
      
      - name: Run Unit Tests
        run: dotnet test --filter "Category!=Performance&Category!=Benchmark" --logger "trx"
      
      - name: Publish Test Results
        uses: EnricoMi/publish-unit-test-result-action@v2
        if: always()
        with:
          files: '**/*.trx'
  
  performance-tests:
    runs-on: ubuntu-latest
    if: github.ref == 'refs/heads/main'
    steps:
      - uses: actions/checkout@v3
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '8.0.x'
      
      - name: Run Performance Tests
        run: dotnet test --filter "Category=Performance&Speed!=Slow" --logger "trx"
```

### Azure DevOps Example

```yaml
trigger:
  - main

pool:
  vmImage: 'ubuntu-latest'

steps:
- task: UseDotNet@2
  inputs:
    version: '8.0.x'

- task: DotNetCoreCLI@2
  displayName: 'Run Unit Tests'
  inputs:
    command: 'test'
    arguments: '--filter "Category!=Performance&Category!=Benchmark" --logger trx'
    
- task: PublishTestResults@2
  inputs:
    testResultsFormat: 'VSTest'
    testResultsFiles: '**/*.trx'
```

---

## See Also

- [API Reference](API-Reference.md)
- [Performance Guide](Performance-Guide.md)
- [Known Issues](Known-Issues.md)
