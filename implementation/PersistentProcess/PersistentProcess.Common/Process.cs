﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using JavaScriptEngineSwitcher.ChakraCore;
using JavaScriptEngineSwitcher.Core;
using Newtonsoft.Json;

namespace Kalmit
{
    public interface IProcess<EventT, ResponseT>
    {
        ResponseT ProcessEvent(EventT serializedEvent);

        string GetSerializedState();

        string SetSerializedState(string serializedState);
    }

    public interface IProcessWithCustomSerialization : IProcess<string, string>
    {
    }

    public interface IDisposableProcessWithCustomSerialization : IProcessWithCustomSerialization, IDisposable
    {
    }

    class ProcessWithCustomSerializationHostedWithPuppeteer : IDisposableProcessWithCustomSerialization
    {
        readonly IJsEngine javascriptEngine;


        public ProcessWithCustomSerializationHostedWithPuppeteer(string javascriptPreparedToRun)
        {
            javascriptEngine = new ChakraCoreJsEngine(
                new ChakraCoreSettings
                {
                    DisableEval = true,
                    EnableExperimentalFeatures = true
                }
            );

            var initAppResult = javascriptEngine.Evaluate(javascriptPreparedToRun);

            var resetAppStateResult = javascriptEngine.Evaluate(
                ProcessFromElm019Code.appStateJsVarName + " = " + ProcessFromElm019Code.initStateJsFunctionPublishedSymbol + ";");
        }

        static string AsJavascriptExpression(string originalString) =>
            JsonConvert.SerializeObject(originalString);

        public void Dispose()
        {
            javascriptEngine?.Dispose();
        }

        public string ProcessEvent(string serializedEvent)
        {
            var eventExpression = AsJavascriptExpression(serializedEvent);

            var expressionJavascript = ProcessFromElm019Code.processEventSyncronousJsFunctionName + "(" + eventExpression + ")";

            return EvaluateInJsEngineAndReturnResultAsString(expressionJavascript);
        }

        public string GetSerializedState()
        {
            var expressionJavascript =
                ProcessFromElm019Code.serializeStateJsFunctionPublishedSymbol +
                "(" + ProcessFromElm019Code.appStateJsVarName + ")";

            return EvaluateInJsEngineAndReturnResultAsString(expressionJavascript);
        }

        public string SetSerializedState(string serializedState)
        {
            var serializedStateExpression = AsJavascriptExpression(serializedState);

            var expressionJavascript =
                ProcessFromElm019Code.appStateJsVarName +
                " = " + ProcessFromElm019Code.deserializeStateJsFunctionPublishedSymbol +
                "(" + serializedStateExpression + ");";

            return EvaluateInJsEngineAndReturnResultAsString(expressionJavascript);
        }

        string EvaluateInJsEngineAndReturnResultAsString(string expressionJavascript)
        {
            var evalResult = javascriptEngine.Evaluate(expressionJavascript);

            return evalResult?.ToString();
        }
    }

    public struct ElmAppEntryConfig
    {
        //  This provides information about entry points in the elm app relative to the files which make up the elm app.

        public ElmAppEntryConfigWithCustomSerialization? WithCustomSerialization;

        public struct ElmAppEntryConfigWithCustomSerialization
        {
            public string pathToFileWithElmEntryPoint;
            public string pathToInitialStateFunction;
            public string pathToSerializedEventFunction;
            public string pathToSerializeStateFunction;
            public string pathToDeserializeStateFunction;
        }
    }

    public class ProcessFromElm019Code
    {
        static public IDisposableProcessWithCustomSerialization WithCustomSerialization(
            IReadOnlyCollection<(string, byte[])> elmCodeFiles,
            ElmAppEntryConfig.ElmAppEntryConfigWithCustomSerialization entryConfig)
        {
            var javascriptFromElmMake = CompileElmToJavascript(elmCodeFiles, entryConfig.pathToFileWithElmEntryPoint);

            var javascriptPreparedToRun =
                BuildAppJavascript(
                    javascriptFromElmMake,
                    entryConfig.pathToSerializedEventFunction,
                    entryConfig.pathToInitialStateFunction,
                    entryConfig.pathToSerializeStateFunction,
                    entryConfig.pathToDeserializeStateFunction);

            return new ProcessWithCustomSerializationHostedWithPuppeteer(javascriptPreparedToRun);
        }

        static string CompileElmToJavascript(
            IReadOnlyCollection<(string, byte[])> elmCodeFiles,
            string pathToFileWithElmEntryPoint)
        {
            var outputFileName = "file-for-elm-make-output.js";

            var command = "make " + pathToFileWithElmEntryPoint + " --output=\"" + outputFileName + "\"";

            var commandResults = ExecutableFile.ExecuteFileWithArguments(
                elmCodeFiles,
                GetElmExecutableFile,
                command,
                new Dictionary<string, string>()
                {
                    //  Avoid elm make failing on `getAppUserDataDirectory`.
                    /* Also, work around problems with elm make like this:
                    -- HTTP PROBLEM ----------------------------------------------------------------

                    The following HTTP request failed:
                        <https://github.com/elm/core/zipball/1.0.0/>

                    Here is the error message I was able to extract:

                    HttpExceptionRequest Request { host = "github.com" port = 443 secure = True
                    requestHeaders = [("User-Agent","elm/0.19.0"),("Accept-Encoding","gzip")]
                    path = "/elm/core/zipball/1.0.0/" queryString = "" method = "GET" proxy =
                    Nothing rawBody = False redirectCount = 10 responseTimeout =
                    ResponseTimeoutDefault requestVersion = HTTP/1.1 } (StatusCodeException
                    (Response {responseStatus = Status {statusCode = 429, statusMessage = "Too
                    Many Requests"}, responseVersion = HTTP/1.1, responseHeaders =
                    [("Server","GitHub.com"),("Date","Sun, 18 Nov 2018 16:53:18
                    GMT"),("Content-Type","text/html"),("Transfer-Encoding","chunked"),("Status","429
                    Too Many
                    Requests"),("Retry-After","120")

                    To avoid elm make failing with this error, break isolation here and reuse elm home directory.
                    An alternative would be retrying when this error is parsed from `commandResults.processOutput.StandardError`.
                    */
                    {"ELM_HOME", GetElmHomeDirectory()},
                });

            var outputFileContent =
                commandResults.resultingFiles.FirstOrDefault(resultFile => resultFile.name == outputFileName).content;

            if (outputFileContent == null)
                throw new NotImplementedException(
                    "Output file not found. Maybe the output from the Elm make process helps to find the cause:" +
                    "\nExit Code: " + commandResults.processOutput.ExitCode +
                    "\nStandard Output:\n'" + commandResults.processOutput.StandardOutput + "'" +
                    "\nStandard Error:\n'" + commandResults.processOutput.StandardError + "'");

            return Encoding.UTF8.GetString(outputFileContent);
        }

        static byte[] GetElmExecutableFile =>
            BlobLibrary.GetBlobWithSHA256(CommonConversion.ByteArrayFromStringBase16(
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ?
                /*
                Elm executable loaded on 2019-01-27 from
                https://github.com/elm/compiler/releases/download/0.19.0/binaries-for-linux.tar.gz
                */
                "280CFC0F4FDE4FC281325A289A645B44468388F25DBF0F87AE72A23DAF822D1A"
                :
                /*
                Elm executable obtained on 2018-09-03 from
                https://github.com/elm/compiler/releases/download/0.19.0/installer-for-windows.exe
                */
                "08931A8DB552E67EF09C4ECD0A9E8E464FFDFF29BC58DAD2990DDE5D4FDC7C6F"));

        public const string appStateJsVarName = "app_state";

        public const string initStateJsFunctionPublishedSymbol = "initState";

        public const string serializedEventFunctionPublishedSymbol = "serializedEvent";

        public const string serializeStateJsFunctionPublishedSymbol = "serializeState";

        public const string deserializeStateJsFunctionPublishedSymbol = "deserializeState";

        /*
        Takes the javascript as emitted from Elm make 0.19 and inserts additional statements to
        prepare the script for usage in our application.
        This preparation includes:
        + Publish interfacing functions of app to the global scope.
        + Add a function which implements processing an event and storing the resulting process state and returns the response of the process.
        */
        static string BuildAppJavascript(
            string javascriptFromElmMake,
            string pathToSerializedEventFunction,
            string pathToInitialStateFunction,
            string pathToSerializeStateFunction,
            string pathToDeserializeStateFunction)
        {
            /*
            Work around runtime exception with javascript from Elm make:
            > "Script threw an exception. 'console' is not defined"

            2018-12-02 Observed problematic statements in output from elm make, causing running the script to fail:
            console.warn('Compiled in DEV mode. Follow the advice at https://elm-lang.org/0.19.0/optimize for better performance and smaller assets.');
            [...]
            console.log(tag + ': ' + _Debug_toString(value));

            For some applications collecting the arguments to those functions might be interesting,
            to implement this, have a look at https://github.com/Microsoft/ChakraCore/wiki/JavaScript-Runtime-(JSRT)-Overview
            */
            var javascriptMinusExceptions =
                Regex.Replace(
                    javascriptFromElmMake,
                    "^\\s*console\\.\\w+\\(.+$", "",
                    RegexOptions.Multiline);

            var invokeExportStatementMatch =
                Regex.Matches(javascriptMinusExceptions, Regex.Escape("_Platform_export(")).OfType<Match>().LastOrDefault();

            var listFunctionToPublish =
                new[]
                {
                    new
                    {
                        name = pathToSerializedEventFunction,
                        publicName = serializedEventFunctionPublishedSymbol,
                        arity = 2,
                    },
                    new
                    {
                        name = pathToInitialStateFunction,
                        publicName = initStateJsFunctionPublishedSymbol,
                        arity = 0,
                    },
                    new
                    {
                        name = pathToSerializeStateFunction,
                        publicName = serializeStateJsFunctionPublishedSymbol,
                        arity = 1,
                    },
                    new
                    {
                        name = pathToDeserializeStateFunction,
                        publicName = deserializeStateJsFunctionPublishedSymbol,
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

            var processEventAndUpdateStateFunctionJavascriptLines = new[]
            {
                "var " + processEventSyncronousJsFunctionName + " = function(eventSerial){",
                "var newStateAndResponse = " + serializedEventFunctionPublishedSymbol + "(eventSerial," + appStateJsVarName + ");",
                appStateJsVarName + " = newStateAndResponse.a;",
                "return newStateAndResponse.b;",
                "}",
            };

            var processEventAndUpdateStateFunctionJavascript =
                String.Join(Environment.NewLine, processEventAndUpdateStateFunctionJavascriptLines);

            return
                javascriptMinusExceptions.Insert(publicationInsertLocation, publicationInsertString) +
                Environment.NewLine +
                processEventAndUpdateStateFunctionJavascript;
        }

        public const string processEventSyncronousJsFunctionName = "processEvent";

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

        static string elmHomeDirectory;

        static string GetElmHomeDirectory()
        {
            elmHomeDirectory = elmHomeDirectory ?? Path.Combine(Filesystem.CreateRandomDirectoryInTempDirectory(), "elm-home");
            Directory.CreateDirectory(elmHomeDirectory);
            return elmHomeDirectory;
        }
    }
}