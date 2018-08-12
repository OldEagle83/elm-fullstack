﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Kalmit;
using Newtonsoft.Json;
using PuppeteerSharp;

namespace PersistentAppFromElmCode.Common
{
    public interface IApp<RequestT, ResponseT>
    {
        ResponseT Request(RequestT serializedRequest);
    }

    public interface IAppWithCustomSerialization : IApp<string, string>
    {
    }

    public interface IDisposableAppWithCustomSerialization : IAppWithCustomSerialization, IDisposable
    {
    }

    class AppWithCustomSerializationHostedWithPuppeteer : IDisposableAppWithCustomSerialization
    {
        readonly Browser browser;

        readonly Page browserPage;

        public AppWithCustomSerializationHostedWithPuppeteer(string javascriptPreparedToRun)
        {
            new BrowserFetcher().DownloadAsync(BrowserFetcher.DefaultRevision).Wait();

            browser = Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
            }).Result;

            browserPage = browser.NewPageAsync().Result;

            var initAppResult = browserPage.EvaluateExpressionAsync(javascriptPreparedToRun).Result;

            var resetAppStateResult = browserPage.EvaluateExpressionAsync(
                PersistentAppFromElm019Code.appStateJsVarName + " = " + "initState;").Result;
        }

        static string AsJavascriptExpression(string originalString) =>
            JsonConvert.SerializeObject(originalString);

        public void Dispose()
        {
            browser?.Dispose();
        }

        public string Request(string serializedRequest)
        {
            var requestExpression = AsJavascriptExpression(serializedRequest);

            var expressionJavascript = PersistentAppFromElm019Code.requestSyncronousJsFunctionName + "(" + requestExpression + ")";

            var processRequestResult = browserPage.EvaluateExpressionAsync<string>(expressionJavascript).Result;

            return processRequestResult;
        }
    }

    public class PersistentAppFromElm019Code
    {
        static public IDisposableAppWithCustomSerialization WithCustomSerialization(
            IReadOnlyCollection<(string, byte[])> elmCodeFiles,
            string pathToFileWithElmEntryPoint,
            string pathToSerializedRequestFunction,
            string pathToInitialStateFunction,
            string pathToSerializeStateFunction,
            string pathToDeserializeStateFunction)
        {
            var javascriptFromElmMake = CompileElmToJavascript(elmCodeFiles, pathToFileWithElmEntryPoint);

            var javascriptPreparedToRun =
                BuildAppJavascript(
                    javascriptFromElmMake,
                    pathToSerializedRequestFunction,
                    pathToInitialStateFunction,
                    pathToSerializeStateFunction,
                    pathToDeserializeStateFunction);

            return new AppWithCustomSerializationHostedWithPuppeteer(javascriptPreparedToRun);
        }

        static string CompileElmToJavascript(
            IReadOnlyCollection<(string, byte[])> elmCodeFiles,
            string pathToFileWithElmEntryPoint)
        {
            var outputFileName = "file-for-elm-make-output.js";

            var command = "make " + pathToFileWithElmEntryPoint + " --output=\"" + outputFileName + "\"";

            var commandResults = ExecutableFile.ExecuteFileWithArguments(elmCodeFiles, GetElmExecutableFile, command);

            var outputFileContent =
                commandResults.resultingFiles.FirstOrDefault(resultFile => resultFile.name == outputFileName).content;

            if (outputFileContent == null)
                throw new NotImplementedException("Output file not found.\nMaybe standard output from compilations helps to find the cause:\n" + commandResults.standardOutput);

            return Encoding.UTF8.GetString(outputFileContent);
        }

        static byte[] GetElmExecutableFile => BlobLibrary.GetBlobWithSHA1(
            CommonConversion.ByteArrayFromStringBase16("612535DC989098715541AC35F321FE2B63116A6B"));

        public const string appStateJsVarName = "app_state";

        public const string serializedRequestFunctionPublishedSymbol = "serializedRequest";

        /*
        Takes the javascript as emitted from Elm make 0.19 and inserts additional statements to
        prepare the script for usage in our application.
        This preparation includes:
        + Publish interfacing functions of app to the global scope.
        + Add a function which implements a request to the app, stores the resulting app state and returns the response of the app.
        */
        static string BuildAppJavascript(
            string javascriptFromElmMake,
            string pathToSerializedRequestFunction,
            string pathToInitialStateFunction,
            string pathToSerializeStateFunction,
            string pathToDeserializeStateFunction)
        {
            var invokeExportStatementMatch =
                Regex.Matches(javascriptFromElmMake, Regex.Escape("_Platform_export(")).OfType<Match>().LastOrDefault();

            var listFunctionToPublish =
                new[]
                {
                    new
                    {
                        name = pathToSerializedRequestFunction,
                        publicName = serializedRequestFunctionPublishedSymbol,
                        arity = 2,
                    },
                    new
                    {
                        name = pathToInitialStateFunction,
                        publicName = "initState",
                        arity = 0,
                    },
                    new
                    {
                        name = pathToSerializeStateFunction,
                        publicName = "serializeState",
                        arity = 1,
                    },
                    new
                    {
                        name = pathToDeserializeStateFunction,
                        publicName = "deserializeState",
                        arity = 1,
                    },
                }
                .Select(
                    functionToPublish =>
                    new
                    {
                        publicName = functionToPublish.publicName,
                        expression =
                            BuildElmFunctionPublicationExpression(
                                appFunctionSymbolMap(functionToPublish.name), functionToPublish.arity)
                    })
                .ToList();

            var publishStatements =
                listFunctionToPublish
                .Select(functionToPublish => "scope['" + functionToPublish.publicName + "'] = " + functionToPublish.expression + ";")
                .ToList();

            var publicationInsertLocation = invokeExportStatementMatch.Index;

            var publicationInsertString =
                string.Join(Environment.NewLine, new[] { "" }.Concat(publishStatements).Concat(new[] { "" }));

            var processRequestAndUpdateStateFunctionJavascriptLines = new[]
            {
                "var " + requestSyncronousJsFunctionName + " = function(requestSerial){",
                "var newStateAndResponse = " + serializedRequestFunctionPublishedSymbol + "(requestSerial," + appStateJsVarName + ");",
                appStateJsVarName + " = newStateAndResponse.a;",
                "return newStateAndResponse.b;",
                "}",
            };

            var processRequestAndUpdateStateFunctionJavascript =
                String.Join(Environment.NewLine, processRequestAndUpdateStateFunctionJavascriptLines);

            return
                javascriptFromElmMake.Insert(publicationInsertLocation, publicationInsertString) +
                Environment.NewLine +
                processRequestAndUpdateStateFunctionJavascript;
        }

        public const string requestSyncronousJsFunctionName = "request";

        static string BuildElmFunctionPublicationExpression(string functionToCallName, int arity)
        {
            if (arity < 2)
                return functionToCallName;

            var paramNameList = Enumerable.Range(0, arity).Select(paramIndex => "param_" + paramIndex).ToList();

            return
                "(" + String.Join(",", paramNameList) + ") => " + functionToCallName +
                String.Join("", paramNameList.Select(paramName => "(" + paramName + ")"));
        }

        static string appFunctionSymbolMap(string pathToFileWithElmEntryPoint) =>
            "author$project$" + pathToFileWithElmEntryPoint.Replace(".", "$");

        static public bool FilePathMatchesPatternOfFilesInElmApp(string filePath) =>
            Regex.IsMatch(
                Path.GetFileName(filePath),
                "(^" + Regex.Escape("elm.json") + "|" + Regex.Escape(".elm") + ")$",
                RegexOptions.IgnoreCase);
    }
}
