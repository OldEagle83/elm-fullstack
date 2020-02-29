using FluentAssertions;
using Kalmit.PersistentProcess.WebHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MoreLinq;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Kalmit.PersistentProcess.Test
{
    [TestClass]
    public class TestWebHost
    {
        [TestMethod]
        public void Web_host_stores_reduction_every_hour()
        {
            var persistentProcessHostDateTime = new DateTimeOffset(2018, 11, 4, 8, 17, 13, TimeSpan.Zero);

            void letTimePassInPersistentProcessHost(TimeSpan amount) =>
                persistentProcessHostDateTime = persistentProcessHostDateTime + amount;

            var allEventsAndExpectedResponses =
                TestSetup.CounterProcessTestEventsAndExpectedResponses(
                    new (int addition, int expectedResponse)[]
                    {
                        (0, 0),
                        (1, 1),
                        (3, 4),
                        (5, 9),
                        (7, 16),
                        (11, 27),
                        (-13, 14),
                    }).ToList();

            var eventsAndExpectedResponsesBatches = allEventsAndExpectedResponses.Batch(3).ToList();

            Assert.IsTrue(2 < eventsAndExpectedResponsesBatches.Count, "More than two batches of events to test with.");

            using (var testSetup = WebHostTestSetup.Setup(
                TestElmWebAppHttpServer.CounterWebApp, () => persistentProcessHostDateTime))
            {
                IEnumerable<string> ReadStoredReductionFileRelativePaths()
                {
                    System.Threading.Thread.Sleep(1111);  //  Storing reduction may be completed after client has received response.
                    return testSetup.BuildProcessStoreInFileDirectory().ReductionsFilesNames();
                }

                using (var server = testSetup.BuildServer())
                {
                    //  Do not depend on an exact number of reductions stored on initialization: Send one request before measuring the number of stored reductions.
                    using (var client = server.CreateClient())
                    {
                        var httpResponse =
                            client.PostAsync("", new StringContent("", System.Text.Encoding.UTF8)).Result;

                        var httpResponseContent = httpResponse.Content.ReadAsStringAsync().Result;
                    }

                    foreach (var eventsAndExpectedResponsesBatch in eventsAndExpectedResponsesBatches)
                    {
                        var beforeBatchStoredReductionsCount = ReadStoredReductionFileRelativePaths().Count();

                        letTimePassInPersistentProcessHost(TimeSpan.FromHours(1));

                        foreach (var (serializedEvent, expectedResponse) in eventsAndExpectedResponsesBatch)
                        {
                            letTimePassInPersistentProcessHost(TimeSpan.FromSeconds(4));

                            using (var client = server.CreateClient())
                            {
                                var httpResponse =
                                    client.PostAsync("", new StringContent(serializedEvent, System.Text.Encoding.UTF8)).Result;

                                var httpResponseContent = httpResponse.Content.ReadAsStringAsync().Result;

                                Assert.AreEqual(expectedResponse, httpResponseContent, false, "server response");
                            }

                            Assert.AreEqual(
                                beforeBatchStoredReductionsCount + 1,
                                ReadStoredReductionFileRelativePaths().Count(),
                                "Number of stored reductions has increased by one since previous batch.");
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void Web_host_continues_composition_log_on_new_file_every_calendar_day()
        {
            var persistentProcessHostDateTime = new DateTimeOffset(2018, 11, 4, 8, 17, 13, TimeSpan.Zero);

            void letTimePassInPersistentProcessHost(TimeSpan amount) =>
                persistentProcessHostDateTime = persistentProcessHostDateTime + amount;

            var allEventsAndExpectedResponses =
                TestSetup.CounterProcessTestEventsAndExpectedResponses(
                    new (int addition, int expectedResponse)[]
                    {
                        (0, 0),
                        (1, 1),
                        (3, 4),
                        (5, 9),
                        (7, 16),
                        (11, 27),
                        (-13, 14),
                    }).ToList();

            var eventsAndExpectedResponsesBatches = allEventsAndExpectedResponses.Batch(3).ToList();

            Assert.IsTrue(2 < eventsAndExpectedResponsesBatches.Count, "More than two batches of events to test with.");

            using (var testSetup = WebHostTestSetup.Setup(
                TestElmWebAppHttpServer.CounterWebApp, () => persistentProcessHostDateTime))
            {
                ProcessStore.ProcessStoreInFileDirectory BuildProcessStore() =>
                    testSetup.BuildProcessStoreInFileDirectory();

                IEnumerable<string> ReadCompositionLogFilesNames() =>
                    BuildProcessStore().EnumerateCompositionsLogFilesNames();

                string FirstReductionRecordFile = null;

                void deleteAllReductionRecordFilesExceptTheFirstOne()
                {
                    var filesNamesExceptTheFirst =
                        BuildProcessStore().ReductionsFilesNames().Except(new[] { FirstReductionRecordFile }).ToList();

                    filesNamesExceptTheFirst.ForEach(File.Delete);

                    Assert.IsTrue(BuildProcessStore().ReductionsFilesNames().Count() <= 1);
                }

                string PostStringContentAndGetResponseStringFromServer(
                    Microsoft.AspNetCore.TestHost.TestServer server, string requestContent)
                {
                    try
                    {
                        using (var client = server.CreateClient())
                        {
                            var httpResponse =
                                client.PostAsync("", new StringContent(requestContent, System.Text.Encoding.UTF8)).Result;

                            return httpResponse.Content.ReadAsStringAsync().Result;
                        }
                    }
                    finally
                    {
                        FirstReductionRecordFile =
                            FirstReductionRecordFile ?? BuildProcessStore().ReductionsFilesNames().FirstOrDefault();
                    }
                }

                using (var server = testSetup.BuildServer())
                {
                    //  Do not depend on an exact number of files stored on initialization: Send one request before measuring the number of stored files.
                    PostStringContentAndGetResponseStringFromServer(server, "");
                }

                foreach (var eventsAndExpectedResponsesBatch in eventsAndExpectedResponsesBatches)
                {
                    var beforeBatchCompositionLogFiles = ReadCompositionLogFilesNames().ToList();

                    letTimePassInPersistentProcessHost(TimeSpan.FromDays(1));

                    var batchDayCompositionLogFileNameExpectedStart =
                        persistentProcessHostDateTime.ToString("yyyy-MM-dd");

                    //  Test that restoring works over all composition log files.
                    deleteAllReductionRecordFilesExceptTheFirstOne();

                    using (var server = testSetup.BuildServer())
                    {
                        foreach (var (serializedEvent, expectedResponse) in eventsAndExpectedResponsesBatch)
                        {
                            letTimePassInPersistentProcessHost(TimeSpan.FromMinutes(4));

                            var httpResponseContent = PostStringContentAndGetResponseStringFromServer(server, serializedEvent);

                            Assert.AreEqual(expectedResponse, httpResponseContent, false, "server response");

                            var inBatchNewCompositionLogFiles =
                                ReadCompositionLogFilesNames().Except(beforeBatchCompositionLogFiles).ToList();

                            Assert.AreEqual(
                                1,
                                inBatchNewCompositionLogFiles.Count,
                                "Exactly one new composition log files since start of batch.");

                            var newCompositionLogFileName = Path.GetFileName(inBatchNewCompositionLogFiles.Single());

                            Assert.IsTrue(
                                newCompositionLogFileName.StartsWith(batchDayCompositionLogFileNameExpectedStart),
                                "File name of new composition log file ('" + newCompositionLogFileName +
                                "') starts with '" + batchDayCompositionLogFileNameExpectedStart + "')");
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void Web_host_serves_static_files_besides_hosting_persistent_process()
        {
            var defaultStaticFile =
                Enumerable.Range(0, 10_000).SelectMany(elem => BitConverter.GetBytes((UInt16)elem))
                .Concat(System.Text.Encoding.UTF8.GetBytes("Default static file content from String\nAnother line"))
                .Concat(Enumerable.Range(0, 100_000).SelectMany(elem => BitConverter.GetBytes((UInt16)elem)))
                .ToArray();

            var defaultStaticFileInSubdirectory = System.Text.Encoding.UTF8.GetBytes("Default static file in 'subdirectory/'.");

            const string processEventPath = "process-event";

            var webAppConfig =
                TestElmWebAppHttpServer.CounterWebApp
                .WithJsonStructure(
                    new WebAppConfigurationJsonStructure
                    {
                        mapsFromRequestUrlToStaticFileName = new[]
                        {
                            new WebAppConfigurationJsonStructure.ConditionalMapFromStringToString
                            {
                                matchingRegexPattern = "^.+/subdirectory/(.+)$",
                                resultString = nameof(defaultStaticFileInSubdirectory),
                            },
                            new WebAppConfigurationJsonStructure.ConditionalMapFromStringToString
                            {
                                matchingRegexPattern = "^(?!.+/" + processEventPath + "$).*",
                                resultString = nameof(defaultStaticFile),
                            },
                        },
                    })
                .WithStaticFiles(
                    new[]
                    {
                        ((IImmutableList<string>)ImmutableList.Create(nameof(defaultStaticFile)), (IImmutableList<byte>)defaultStaticFile.ToImmutableList()),
                        (ImmutableList.Create(nameof(defaultStaticFileInSubdirectory)), defaultStaticFileInSubdirectory.ToImmutableList()),
                    });

            var allEventsAndExpectedResponses =
                TestSetup.CounterProcessTestEventsAndExpectedResponses(
                    new (int addition, int expectedResponse)[]
                    {
                        (0, 0),
                        (1, 1),
                        (3, 4),
                        (5, 9),
                        (7, 16),
                        (11, 27),
                        (-13, 14),
                    }).ToList();

            using (var testSetup = WebHostTestSetup.Setup(webAppConfig))
            {
                string PostAppEventAndGetResponseStringFromServer(
                    Microsoft.AspNetCore.TestHost.TestServer server, string appEvent)
                {
                    using (var client = server.CreateClient())
                    {
                        var httpResponse =
                            client.PostAsync(processEventPath, new StringContent(appEvent, System.Text.Encoding.UTF8)).Result;

                        return httpResponse.Content.ReadAsStringAsync().Result;
                    }
                }

                using (var server = testSetup.BuildServer())
                {
                    using (var client = server.CreateClient())
                    {
                        void assertHttpResponseForStaticFile(string path, byte[] expectedFile)
                        {
                            try
                            {
                                var httpResponse = client.GetAsync(path).Result;

                                Assert.AreEqual(HttpStatusCode.OK, httpResponse.StatusCode, "HTTP GET response status code.");

                                var responseContent = httpResponse.Content.ReadAsByteArrayAsync().Result;

                                CollectionAssert.AreEqual(expectedFile, responseContent, "HTTP GET response content.");

                                Assert.AreEqual(
                                    HttpStatusCode.MethodNotAllowed,
                                    client.PostAsync(path, new StringContent("")).Result.StatusCode,
                                    "HTTP POST response status code.");

                                Assert.AreEqual(
                                    HttpStatusCode.MethodNotAllowed,
                                    client.PutAsync(path, new StringContent("")).Result.StatusCode,
                                    "HTTP PUT response status code.");
                            }
                            catch (Exception e)
                            {
                                throw new Exception("Failed for static file at path '" + path + "'", e);
                            }
                        }

                        Assert.ThrowsException<Exception>(
                            () => assertHttpResponseForStaticFile(
                                "", System.Text.Encoding.UTF8.GetBytes("This should not match")));

                        foreach (var pathWhichShouldBeMappedToDefaultStaticFile in new[] { "", "index.html", "almost-anything-else-too" })
                        {
                            assertHttpResponseForStaticFile(
                                pathWhichShouldBeMappedToDefaultStaticFile, defaultStaticFile);
                        }

                        foreach (var pathWhichShouldBeMappedToDefaultStaticFile in new[] { "subdirectory/a-path", "subdirectory/another-path" })
                        {
                            assertHttpResponseForStaticFile(
                                pathWhichShouldBeMappedToDefaultStaticFile, defaultStaticFileInSubdirectory);
                        }
                    }

                    foreach (var (serializedEvent, expectedResponse) in allEventsAndExpectedResponses)
                    {
                        var httpResponseContent = PostAppEventAndGetResponseStringFromServer(server, serializedEvent);

                        Assert.AreEqual(expectedResponse, httpResponseContent, false, "server response");
                    }
                }
            }
        }

        [TestMethod]
        public void Web_host_can_be_configured_to_serve_static_files_without_elm_app()
        {
            var defaultStaticFile =
                Enumerable.Range(0, 10_000).SelectMany(elem => BitConverter.GetBytes((UInt16)elem))
                .Concat(System.Text.Encoding.UTF8.GetBytes("Default static file content from String\nAnother line"))
                .Concat(Enumerable.Range(0, 100_000).SelectMany(elem => BitConverter.GetBytes((UInt16)elem)))
                .ToArray();

            var webAppConfig =
                new WebAppConfiguration()
                .WithJsonStructure(
                    new WebAppConfigurationJsonStructure
                    {
                        mapsFromRequestUrlToStaticFileName = new[]
                        {
                            new WebAppConfigurationJsonStructure.ConditionalMapFromStringToString
                            {
                                matchingRegexPattern = ".*",
                                resultString = nameof(defaultStaticFile),
                            },
                        },
                    })
                .WithStaticFiles(
                    new[]
                    {
                        ((IImmutableList<string>)ImmutableList.Create(nameof(defaultStaticFile)), (IImmutableList<byte>)defaultStaticFile.ToImmutableList()),
                    });

            using (var testSetup = WebHostTestSetup.Setup(webAppConfig))
            {
                using (var server = testSetup.BuildServer())
                {
                    using (var client = server.CreateClient())
                    {
                        foreach (var pathWhichShouldBeMappedToDefaultStaticFile in new[] { "", "index.html", "almost-anything-else-too" })
                        {
                            var response = client.GetAsync(pathWhichShouldBeMappedToDefaultStaticFile).Result;

                            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Response status code");
                            CollectionAssert.AreEqual(
                                defaultStaticFile,
                                response.Content.ReadAsByteArrayAsync().Result,
                                "Response Content");
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void Web_host_rate_limits_requests_before_reaching_persistent_process()
        {
            const int requestBatchSize = 100;
            const int minimumNumberOfRequestsInFastBatchExpectedToBeBlockedByRateLimit = 85;
            const int rateLimitWindowSize = 10;

            var persistentProcessHostDateTime = new DateTimeOffset(2018, 11, 12, 19, 51, 13, TimeSpan.Zero);

            void letTimePassInPersistentProcessHost(TimeSpan amount) =>
                persistentProcessHostDateTime = persistentProcessHostDateTime + amount;

            var requestsBatches =
                Enumerable.Range(0, 3)
                .Select(batchIndex =>
                    Enumerable.Range(0, requestBatchSize)
                    .Select(indexInBatch => "Batch " + batchIndex + ", Event " + indexInBatch).ToList())
                    .ToList();

            var webAppConfig =
                TestElmWebAppHttpServer.CounterWebApp
                .WithJsonStructure(
                    new WebAppConfigurationJsonStructure
                    {
                        singleRateLimitWindowPerClientIPv4Address = new WebAppConfigurationJsonStructure.RateLimitWindow
                        {
                            windowSizeInMs = 1000 * rateLimitWindowSize,
                            limit = rateLimitWindowSize,
                        },
                    });

            using (var testSetup = WebHostTestSetup.Setup(webAppConfig, () => persistentProcessHostDateTime))
            {
                IEnumerable<string> EnumerateStoredProcessEventsHttpRequestsBodies() =>
                    testSetup.EnumerateStoredProcessEventsReverse()
                    .Select(processEvent => processEvent?.httpRequest?.request?.bodyAsString)
                    .WhereNotNull();

                HttpResponseMessage PostStringContentToServer(
                    Microsoft.AspNetCore.TestHost.TestServer server, string requestContent)
                {
                    using (var client = server.CreateClient())
                    {
                        return
                            client.PostAsync("", new StringContent(requestContent, System.Text.Encoding.UTF8)).Result;
                    }
                }

                IEnumerable<HttpResponseMessage> whereStatusCodeTooManyRequests(
                    IEnumerable<HttpResponseMessage> httpResponses) =>
                    httpResponses.Where(httpResponse => httpResponse.StatusCode == HttpStatusCode.TooManyRequests);

                using (var server = testSetup.BuildServer())
                {
                    {
                        var firstBatchHttpResponses =
                            requestsBatches[0].Select(processEvent =>
                            {
                                letTimePassInPersistentProcessHost(TimeSpan.FromSeconds(1));

                                return PostStringContentToServer(server, processEvent);
                            }).ToList();

                        whereStatusCodeTooManyRequests(firstBatchHttpResponses).Count()
                        .Should().Be(0, "No HTTP response from the first batch has status code 'Too Many Requests'.");

                        CollectionAssert.IsSubsetOf(
                            requestsBatches[0],
                            EnumerateStoredProcessEventsHttpRequestsBodies().ToList(),
                            "All events from the first batch have been stored.");
                    }

                    {
                        var secondBatchHttpResponses =
                            requestsBatches[1].Select(processEvent =>
                            {
                                //  Process the events in the second batch in smaller timespan so that effect of the rate-limit should be observable.
                                letTimePassInPersistentProcessHost(TimeSpan.FromSeconds(0.1));

                                return PostStringContentToServer(server, processEvent);
                            }).ToList();

                        whereStatusCodeTooManyRequests(secondBatchHttpResponses).Count()
                        .Should().BeGreaterThan(minimumNumberOfRequestsInFastBatchExpectedToBeBlockedByRateLimit,
                            "At least this many requests in the fast batch are expected to be answered with status code 'Too Many Requests'.");

                        requestsBatches[1].Except(EnumerateStoredProcessEventsHttpRequestsBodies()).Count()
                        .Should().BeGreaterThan(minimumNumberOfRequestsInFastBatchExpectedToBeBlockedByRateLimit,
                            "At least this many requests in the fast batch are expected to be filtered out by rate-limit before reaching the persistent process.");
                    }

                    {
                        letTimePassInPersistentProcessHost(TimeSpan.FromSeconds(rateLimitWindowSize));

                        var thirdBatchHttpResponses =
                            requestsBatches[2].Select(processEvent =>
                            {
                                letTimePassInPersistentProcessHost(TimeSpan.FromSeconds(1));

                                return PostStringContentToServer(server, processEvent);
                            }).ToList();

                        whereStatusCodeTooManyRequests(thirdBatchHttpResponses).Count()
                        .Should().Be(0, "No HTTP response from the third batch has status code 'Too Many Requests'.");

                        CollectionAssert.IsSubsetOf(
                            requestsBatches[2],
                            EnumerateStoredProcessEventsHttpRequestsBodies().ToList(),
                            "All events from the third batch have been stored.");
                    }
                }
            }
        }

        [TestMethod]
        public void Web_host_supports_setting_persistent_process_state_only_after_authorization()
        {
            const string rootPassword = "Root-Password_1234567";

            Func<IWebHostBuilder, IWebHostBuilder> webHostBuilderMap =
                builder => builder.WithSettingAdminRootPassword(rootPassword);

            using (var testSetup = WebHostTestSetup.Setup(TestElmWebAppHttpServer.StringBuilderWebApp, webHostBuilderMap))
            {
                using (var server = testSetup.BuildServer())
                {
                    Assert.AreEqual(
                        "",
                        HttpGetAtRoot(server).Content.ReadAsStringAsync().Result,
                        "Initial State");

                    Assert.AreEqual(HttpStatusCode.OK, HttpPostStringContentAtRoot(server, "part-a").StatusCode);

                    Assert.AreEqual(HttpStatusCode.OK, HttpPostStringContentAtRoot(server, "-part-b").StatusCode);

                    Assert.AreEqual(
                        "part-a-part-b",
                        HttpGetAtRoot(server).Content.ReadAsStringAsync().Result,
                        "State After Multiple Posts");

                    using (var client = server.CreateClient())
                    {
                        Assert.AreEqual(
                            HttpStatusCode.Unauthorized,
                            HttpSetPersistentProcessState(client, "new-state").Result.StatusCode,
                                "HTTP status code for unauthorized request to set persistent process state.");

                        Assert.AreEqual(
                            "part-a-part-b",
                            HttpGetAtRoot(server).Content.ReadAsStringAsync().Result,
                            "State after failing to set persistent process state.");

                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                            "Basic",
                            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                                WebHost.Configuration.BasicAuthenticationForAdminRoot(rootPassword))));

                        Assert.AreEqual(
                            HttpStatusCode.OK,
                            HttpSetPersistentProcessState(client, @"""new-state""").Result.StatusCode,
                                "HTTP status code for authorized request to set persistent process state.");
                    }

                    Assert.AreEqual(
                        "new-state",
                        HttpGetAtRoot(server).Content.ReadAsStringAsync().Result,
                        "State after setting persistent process state.");

                    Assert.AreEqual(HttpStatusCode.OK, HttpPostStringContentAtRoot(server, "_appendix").StatusCode);
                }

                using (var server = testSetup.BuildServer())
                {
                    Assert.AreEqual(
                        "new-state_appendix",
                        HttpGetAtRoot(server).Content.ReadAsStringAsync().Result,
                        "State after setting persistent process state, appending, and restarting server.");
                }
            }
        }

        [TestMethod]
        public void Web_host_propagates_HTTP_headers()
        {
            // This name needs to be consistent with the code in Elm app CrossPropagateHttpHeadersToAndFromBody.
            const string appSpecificHttpResponseHeaderName = "response-header-name";

            using (var testSetup = WebHostTestSetup.Setup(TestElmWebAppHttpServer.CrossPropagateHttpHeadersToAndFromBody, builder => builder))
            {
                using (var server = testSetup.BuildServer())
                {
                    using (var client = server.CreateClient())
                    {
                        var requestHeaderValue = "HTTP request header value.";
                        var requestContentString = "HTTP request content.";

                        const string appSpecificHttpRequestHeaderName = "request-header-name";

                        client.DefaultRequestHeaders.Add(appSpecificHttpRequestHeaderName, WebUtility.UrlEncode(requestHeaderValue));

                        var response = client.PostAsync("", new StringContent(requestContentString)).Result;

                        Assert.AreEqual("application/json", response.Content.Headers.ContentType.ToString());

                        var responseContentString = response.Content.ReadAsStringAsync().Result;

                        var collectionFromResponseContent =
                            Newtonsoft.Json.JsonConvert.DeserializeObject<Web_host_propagates_HTTP_headers_Response_Entry[]>(
                                responseContentString);

                        var matchingEntryFromResponseContent =
                            collectionFromResponseContent
                            .First(entry => entry.name == appSpecificHttpRequestHeaderName);

                        Assert.AreEqual(
                            requestHeaderValue,
                            WebUtility.UrlDecode(matchingEntryFromResponseContent.values.FirstOrDefault()),
                            "Expect the HTTP request header was propagated to an entry in the response content.");

                        response.Headers.TryGetValues(appSpecificHttpResponseHeaderName, out var appSpecificHttpHeaderValues);

                        Assert.AreEqual(
                            requestContentString,
                            WebUtility.UrlDecode(appSpecificHttpHeaderValues?.FirstOrDefault()),
                            "Expect the HTTP request content was propagated to the response header with name '" + appSpecificHttpResponseHeaderName + "'");
                    }
                }
            }
        }

        [TestMethod]
        public void Host_supports_sending_HTTP_requests()
        {
            const string echoServerUrl = "http://localhost:6789/";

            var echoWebHostBuilder =
                Microsoft.AspNetCore.WebHost.CreateDefaultBuilder()
                .Configure(app =>
                {
                    app.Run(async (context) =>
                    {
                        var requestRecord = Asp.AsPersistentProcessInterfaceHttpRequest(context.Request);

                        context.Response.StatusCode = 200;

                        await context.Response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(requestRecord));
                    });
                })
                .UseUrls(echoServerUrl);

            using (var echoServer = echoWebHostBuilder.Build())
            {
                echoServer.Start();

                using (var testSetup = WebHostTestSetup.Setup(TestElmWebAppHttpServer.HttpProxyWebApp, builder => builder))
                {
                    using (var server = testSetup.BuildServer())
                    {
                        using (var client = server.CreateClient())
                        {
                            var customHeaderName = "My-custom-header";
                            var customHeaderValue = "Hello!";
                            var requestContentString = "HTTP request content.";

                            var httpRequestMessage = new HttpRequestMessage(new HttpMethod("post"), "")
                            {
                                Content = new StringContent(requestContentString),
                            };

                            httpRequestMessage.Headers.Add("forward-to", echoServerUrl);

                            httpRequestMessage.Headers.Add(customHeaderName, customHeaderValue);

                            var response = client.SendAsync(httpRequestMessage).Result;

                            var responseContentString = response.Content.ReadAsStringAsync().Result;

                            var echoRequestStructure =
                                Newtonsoft.Json.JsonConvert.DeserializeObject<InterfaceToHost.HttpRequest>(responseContentString);

                            Assert.AreEqual(
                                requestContentString,
                                echoRequestStructure.bodyAsString,
                                "Request body is propagated.");

                            var echoCustomHeaderValues =
                                echoRequestStructure.headers
                                .FirstOrDefault(header => header.name == customHeaderName)
                                ?.values;

                            Assert.AreEqual(
                                1,
                                echoCustomHeaderValues?.Length,
                                "Custom header name is propagated.");

                            Assert.AreEqual(
                                customHeaderValue,
                                echoCustomHeaderValues[0],
                                "Custom header value is propagated.");
                        }

                        using (var client = server.CreateClient())
                        {
                            var contentTypeHeaderName = "content-type";
                            var customContentType = "application/octet-stream";

                            var httpRequestMessage = new HttpRequestMessage(new HttpMethod("post"), "")
                            {
                                Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("Hello!")),
                            };

                            httpRequestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue(customContentType);

                            httpRequestMessage.Headers.Add("forward-to", echoServerUrl);

                            var response = client.SendAsync(httpRequestMessage).Result;

                            var responseContentString = response.Content.ReadAsStringAsync().Result;

                            var echoRequestStructure =
                                Newtonsoft.Json.JsonConvert.DeserializeObject<InterfaceToHost.HttpRequest>(responseContentString);

                            var observedContentType =
                                echoRequestStructure.headers
                                .SingleOrDefault(header => string.Equals(header.name, contentTypeHeaderName, StringComparison.InvariantCultureIgnoreCase))
                                ?.values.SingleOrDefault();

                            Assert.AreEqual(
                                customContentType,
                                observedContentType,
                                "Sent HTTP request with content type '" + customContentType + "'.");
                        }
                    }
                }
            }
        }

        class Web_host_propagates_HTTP_headers_Response_Entry
        {
            public string name = null;

            public string[] values = null;
        }

        static HttpResponseMessage HttpGetAtRoot(
            Microsoft.AspNetCore.TestHost.TestServer server)
        {
            using (var client = server.CreateClient())
            {
                return client.GetAsync("").Result;
            }
        }

        static HttpResponseMessage HttpPostStringContentAtRoot(
            Microsoft.AspNetCore.TestHost.TestServer server, string requestContent)
        {
            using (var client = server.CreateClient())
            {
                return
                    client.PostAsync("", new StringContent(requestContent, System.Text.Encoding.UTF8)).Result;
            }
        }

        static System.Threading.Tasks.Task<HttpResponseMessage> HttpSetPersistentProcessState(
            HttpClient client,
            string state) =>
            client.PostAsync(
                Kalmit.PersistentProcess.WebHost.Configuration.AdminPath +
                Kalmit.PersistentProcess.WebHost.Configuration.ApiPersistentProcessStatePath,
                new StringContent(state, System.Text.Encoding.UTF8));
    }
}