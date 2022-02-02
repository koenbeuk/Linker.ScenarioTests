﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ScenarioTests.Internal
{
    sealed internal class ScenarioFactTestCaseRunner : XunitTestCaseRunner
    {
        public ScenarioFactTestCaseRunner(IXunitTestCase testCase,
                                         string displayName,
                                         string skipReason,
                                         object[] constructorArguments,
                                         IMessageSink diagnosticMessageSink,
                                         IMessageBus messageBus,
                                         ExceptionAggregator aggregator,
                                         CancellationTokenSource cancellationTokenSource)
            : base(testCase, displayName, skipReason, constructorArguments, Array.Empty<object>(), messageBus, aggregator, cancellationTokenSource)
        {
            DiagnosticMessageSink = diagnosticMessageSink;
        }

        /// <summary>
        /// Gets the message sink used to report <see cref="IDiagnosticMessage"/> messages.
        /// </summary>
        public IMessageSink DiagnosticMessageSink { get; }

        protected override async Task<RunSummary> RunTestAsync()
        {
            var scenarioFactTestCase = (ScenarioFactTestCase)TestCase;
            var test = CreateTest(TestCase, DisplayName);
            var aggregatedResult = new RunSummary();

            // Theories are called with required arguments. Keep track of what arguments we already tested so that we can skip those accordingly
            var testedArguments = new HashSet<object>();

            // Each time we find a new theory argument, we will want to restart our Test so that we can collect subsequent test cases
            bool pendingRestart;

            do
            {
                // Safeguarding against abuse
                if (testedArguments.Count >= scenarioFactTestCase.TheoryTestCaseLimit)
                {
                    pendingRestart = false;
                    MessageBus.QueueMessage(new TestSkipped(test, "Theory tests are capped to prevent infinite loops. You can configure a different limit by setting TheoryTestCaseLimit on the Scenario attribute"));
                    aggregatedResult.Aggregate(new RunSummary
                    {
                        Skipped = 1,
                        Total = 1
                    });
                }
                else
                {
                    var bufferedMessageBus = new BufferedMessageBus(MessageBus);
                    var stopwatch = Stopwatch.StartNew();
                    var skipAdditionalTests = false;
                    var testRecorded = false;
                    pendingRestart = false; // By default we dont expect a new restart

                    object? capturedArgument = null;
                    ScenarioContext scenarioContext = null;

                    scenarioContext = new ScenarioContext(scenarioFactTestCase.FactName, async (ScenarioTestCaseDescriptor descriptor) =>
                    {
                        // If we're hitting our target test
                        if (descriptor.Name == scenarioFactTestCase.FactName)
                        {
                            testRecorded = true;

                            if (skipAdditionalTests)
                            {
                                pendingRestart = true; // when we discovered more tests after a test completed, allow us to restart
                                scenarioContext.EndScenarioConditionally();
                                return;
                            }

                            if (descriptor.Argument is not null)
                            {
                                // If we've already received this test case, don't run it again
                                if (testedArguments.Contains(descriptor.Argument))
                                {
                                    return;
                                }

                                testedArguments.Add(descriptor.Argument);
                                capturedArgument = descriptor.Argument;
                            }

                            // At this stage we found our first valid test case, any subsequent test case should issue a restart instead
                            skipAdditionalTests = true;
                            try
                            {
                                await descriptor.Invocation();
                            }
                            catch (Exception)
                            {
                                // If we caught an exception but we're in a theory, we will want to try for additional test cases
                                if (descriptor.Argument is not null)
                                {
                                    pendingRestart = true;
                                }

                                throw;
                            }
                            finally
                            {
                                scenarioContext.IsTargetConclusive = true;
                            }
                        }
                        else
                        {
                            // We may be hitting a shared fact, those need to be invoked as well but not recorded as our primary target
                            if (!scenarioFactTestCase.RunInIsolation || descriptor.Flags.HasFlag(ScenarioTestCaseFlags.Shared))
                            {
                                await descriptor.Invocation();
                            }
                        }
                    });

                    scenarioContext.AutoAbort = scenarioFactTestCase.ExecutionPolicy is ScenarioTestExecutionPolicy.EndAfterConclusion;

                    TestMethodArguments = new object[] { scenarioContext };

                    RunSummary result;

                    result = await CreateTestRunner(test, bufferedMessageBus, TestClass, ConstructorArguments, TestMethod, TestMethodArguments, SkipReason, BeforeAfterAttributes, Aggregator, CancellationTokenSource).RunAsync();

                    aggregatedResult.Aggregate(result);

                    stopwatch.Stop();
                    var testInvocationTest = capturedArgument switch
                    {
                        null => CreateTest(TestCase, DisplayName),
                        not null => CreateTest(TestCase, $"{DisplayName} ({capturedArgument})")
                    };

                    var bufferedMessages = bufferedMessageBus.QueuedMessages;

                    // We should have expected at least one test run. We probably returned before our target test was able to run
                    if (!testRecorded && result.Failed == 0)
                    {
                        bufferedMessageBus.QueueMessage(new TestSkipped(test, scenarioContext.SkippedReason ?? "No applicable tests were able to run"));
                        result = new RunSummary { Skipped = 1, Total = 1 };
                    }

                    // If we skipped this test, make sure that this is reported accordingly
                    if (scenarioContext.Skipped && !bufferedMessages.OfType<TestSkipped>().Any())
                    {
                        bufferedMessages = bufferedMessages.Concat(new[] { new TestSkipped(testInvocationTest, scenarioContext.SkippedReason) });
                    }

                    // If we have indeed skipped this test, make sure that we're not reporting it as passed or failed
                    if (bufferedMessages.OfType<TestSkipped>().Any())
                    {
                        bufferedMessages = bufferedMessages.Where(x => x is not TestPassed and not TestFailed);
                    }

                    // If we have a failure in post conditions, don't mark this test case as passed
                    if (bufferedMessages.OfType<TestFailed>().Any())
                    {
                        bufferedMessages = bufferedMessages.Where(x => x is not TestPassed);
                    }

                    var output = string.Join("", bufferedMessages
                        .OfType<ITestOutput>()
                        .Select(x => x.Output));

                    var duration = (decimal)stopwatch.Elapsed.TotalSeconds;

                    foreach (var queuedMessage in bufferedMessages)
                    {
                        var transformedMessage = queuedMessage switch
                        {
                            TestStarting testStarting => new TestStarting(testInvocationTest),
                            TestSkipped testSkipped => new TestSkipped(testInvocationTest, testSkipped.Reason),
                            TestPassed testPassed => new TestPassed(testInvocationTest, duration, output),
                            TestFailed testFailed => new TestFailed(testInvocationTest, duration, output, testFailed.ExceptionTypes, testFailed.Messages, testFailed.StackTraces, testFailed.ExceptionParentIndices),
                            TestFinished testFinished => new TestFinished(testInvocationTest, duration, output),
                            _ => queuedMessage
                        };

                        if (!MessageBus.QueueMessage(transformedMessage))
                        {
                            return aggregatedResult;
                        }
                    }
                }
            }
            while (pendingRestart);

            return aggregatedResult;
        }
    }
}
