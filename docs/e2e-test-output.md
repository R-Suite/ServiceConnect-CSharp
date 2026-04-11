[xUnit.net 00:00:00.00] xUnit.net VSTest Adapter v2.8.2+699d445a1a (64-bit .NET 10.0.5)
[xUnit.net 00:00:00.04]   Discovering: ServiceConnect.EndToEndTests
[xUnit.net 00:00:00.07]   Discovered:  ServiceConnect.EndToEndTests
[xUnit.net 00:00:00.07]   Starting:    ServiceConnect.EndToEndTests
[testcontainers.org 00:00:00.09] Connected to Docker:
  Host: unix:///var/run/docker.sock
  Server Version: 29.3.1
  Kernel Version: 6.17.0-20-generic
  API Version: 1.54
  Operating System: Ubuntu 24.04.4 LTS
  Total Memory: 31.05 GB
[xUnit.net 00:00:00.35]     ServiceConnect.EndToEndTests.PublishAfterDisposeTests.PublishAsync_AfterDispose_Throws [FAIL]
[xUnit.net 00:00:00.36]       Assert.ThrowsAny() Failure: No exception was thrown
[xUnit.net 00:00:00.36]       Expected: typeof(System.Exception)
[xUnit.net 00:00:00.36]       Stack Trace:
[xUnit.net 00:00:00.36]         /home/tim/source/ServiceConnect-CSharp/src/ServiceConnect.EndToEndTests/PublishAfterDisposeTests.cs(30,0): at ServiceConnect.EndToEndTests.PublishAfterDisposeTests.PublishAsync_AfterDispose_Throws()
[xUnit.net 00:00:00.36]         --- End of stack trace from previous location ---
[testcontainers.org 00:00:00.26] Docker container 1d310ed7d0ca created
[testcontainers.org 00:00:00.27] Start Docker container 1d310ed7d0ca
[testcontainers.org 00:00:00.38] Wait for Docker container 1d310ed7d0ca to complete readiness checks
[testcontainers.org 00:00:00.38] Docker container 1d310ed7d0ca ready
[testcontainers.org 00:00:00.44] Docker container fc6ec0d54da1 created
[testcontainers.org 00:00:00.45] Start Docker container fc6ec0d54da1
[testcontainers.org 00:00:00.46] Docker container 23b466cebbb5 created
[testcontainers.org 00:00:00.46] Start Docker container 23b466cebbb5
[testcontainers.org 00:00:00.46] Docker container b2e1d6c7c777 created
[testcontainers.org 00:00:00.46] Start Docker container b2e1d6c7c777
[testcontainers.org 00:00:00.55] Wait for Docker container fc6ec0d54da1 to complete readiness checks
[testcontainers.org 00:00:00.56] Wait for Docker container b2e1d6c7c777 to complete readiness checks
[testcontainers.org 00:00:00.58] Wait for Docker container 23b466cebbb5 to complete readiness checks
[testcontainers.org 00:00:03.58] Docker container b2e1d6c7c777 ready
[testcontainers.org 00:00:03.58] Docker container fc6ec0d54da1 ready
[testcontainers.org 00:00:03.59] Docker container 23b466cebbb5 ready
[xUnit.net 00:00:03.76]     ServiceConnect.EndToEndTests.AggregatorExceptionTests.Aggregator_ExecuteThrows_MessageSentToErrorQueue [SKIP]
[xUnit.net 00:00:03.76]       Aggregator Execute exceptions are swallowed by InMemory persistor state — message re-inserted on retry causes infinite retry loop. Needs framework fix.
[testcontainers.org 00:00:30.64] Delete Docker container b2e1d6c7c777
[testcontainers.org 00:00:30.64] Delete Docker container fc6ec0d54da1
[testcontainers.org 00:10:32.15] Delete Docker container 23b466cebbb5
[xUnit.net 00:10:32.54]   Finished:    ServiceConnect.EndToEndTests
  ServiceConnect.EndToEndTests test net10.0 failed with 1 error(s) (633.0s)
    /home/tim/source/ServiceConnect-CSharp/src/ServiceConnect.EndToEndTests/PublishAfterDisposeTests.cs(30): error TESTERROR: 
      ServiceConnect.EndToEndTests.PublishAfterDisposeTests.PublishAsync_AfterDispose_Throws (232ms): Error Message: Assert.ThrowsAny() Failure: No exception was thrown
      Expected: typeof(System.Exception)
      Stack Trace:
         at ServiceConnect.EndToEndTests.PublishAfterDisposeTests.PublishAsync_AfterDispose_Throws() in /home/tim/source/ServiceConnect-CSharp/src/ServiceConnect.EndToEndTests/PublishAfterDisposeTests.cs:line 30
      --- End of stack trace from previous location ---

Test summary: total: 66, failed: 1, succeeded: 64, skipped: 1, duration: 632.9s
Build failed with 1 error(s) and 49 warning(s) in 639.0s