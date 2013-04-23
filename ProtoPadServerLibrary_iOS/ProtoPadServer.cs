﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using MonoTouch.UIKit;
using ServiceDiscovery;

namespace ProtoPadServerLibrary_iOS
{
    public class ProtoPadServer : IDisposable
    {
        public IPAddress LocalIPAddress { get; private set; }
        public int ListeningPort { get; private set; }
        public string BroadcastedAppName { get; private set; }

        private readonly UIWindow _window;
        private readonly SimpleHttpServer _httpServer;
        private readonly UdpDiscoveryServer _udpServer;

        /// <summary>
        /// Starts listening for ProtoPad clients, and allows them to connect and access the UIWindow you pass in
        /// Add the following values to your Info.plist if you want to enable iTunes access to files
        /// <key>UIFileSharingEnabled</key>
        /// <string>YES</string>
        /// </summary>
        /// <param name="window">Supply your main application window here. This will be made scriptable from the ProtoPad Client.</param>
        public static ProtoPadServer Create(UIWindow window, int? overrideListeningPort = null, string overrideBroadcastedAppName = null)
        {
            return new ProtoPadServer(window, overrideListeningPort, overrideBroadcastedAppName);
        }

        private ProtoPadServer(UIWindow window, int? overrideListeningPort = null, string overrideBroadcastedAppName = null)
        {
            _window = window;

            BroadcastedAppName = overrideBroadcastedAppName ?? String.Format("ProtoPad Service on iOS Device {0}", UIDevice.CurrentDevice.Name);
            ListeningPort = overrideListeningPort ?? 8080;
            LocalIPAddress = Helpers.GetCurrentIPAddress();

            _httpServer = new SimpleHttpServer(responseBytes =>
            {
                var response = "{}";
                var remoteCommandDoneEvent = new AutoResetEvent(false);
                _window.InvokeOnMainThread(() => Response(responseBytes, remoteCommandDoneEvent, ref response));
                remoteCommandDoneEvent.WaitOne();
                return response;
            }, ListeningPort, "iOS");

            _udpServer = new UdpDiscoveryServer(BroadcastedAppName, String.Format("http://{0}:{1}/", LocalIPAddress, ListeningPort), Helpers.GetCurrentIPAddress());          
        }

        public static string JsonEncode(object value)
        {
            var serializer = new DataContractJsonSerializer(value.GetType());
            string resultJSON;
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                resultJSON = Encoding.Default.GetString(stream.ToArray());
                stream.Close();
            }
            return resultJSON;
        }

        private void Response(byte[] responseBytes, EventWaitHandle remoteCommandDoneEvent, ref string response)
        {
            try
            {
                var executeResponse = ExecuteLoadedAssemblyString(responseBytes, _window);
                if (executeResponse.DumpValues != null)
                {
                    executeResponse.Results = executeResponse.DumpValues.Select(v => new Tuple<string, DumpValue>(v.Item1, Dumper.ObjectToDumpValue(v.Item2, v.Item3, executeResponse.MaxEnumerableItemCount))).ToList();
                }
                response = JsonEncode(executeResponse);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
            finally
            {
                remoteCommandDoneEvent.Set();
            }
        }

        private class ExecuteResponse
        {
            public string ErrorMessage { get; set; }
            public List<Tuple<string, object, int, bool>> DumpValues;
            public List<Tuple<string, DumpValue>> Results { get; set; }
            public int MaxEnumerableItemCount;
        }

        private static ExecuteResponse ExecuteLoadedAssemblyString(byte[] loadedAssemblyBytes, UIWindow window)
        {
            MethodInfo printMethod;

            object loadedInstance;
            try
            {
                // TODO: create new AppDomain for each loaded assembly, to prevent memory leakage
                var loadedAssembly = AppDomain.CurrentDomain.Load(loadedAssemblyBytes);
                var loadedType = loadedAssembly.GetType("__MTDynamicCode");
                if (loadedType == null) return null;
                loadedInstance = Activator.CreateInstance(loadedType);
                printMethod = loadedInstance.GetType().GetMethod("Main");
            }
            catch (Exception e)
            {
                return new ExecuteResponse { ErrorMessage = e.Message };
            }

            var response = new ExecuteResponse();
            try
            {
                printMethod.Invoke(loadedInstance, new object[] { window });
                response.DumpValues = loadedInstance.GetType().GetField("___dumps").GetValue(loadedInstance) as List<Tuple<string, object, int, bool>>;
                response.MaxEnumerableItemCount = Convert.ToInt32(loadedInstance.GetType().GetField("___maxEnumerableItemCount").GetValue(loadedInstance));
            }
            catch (Exception e)
            {
                var lineNumber = loadedInstance.GetType().GetField("___lastExecutedStatementOffset").GetValue(loadedInstance);
                response.ErrorMessage = String.Format("___EXCEPTION_____At offset: {0}__{1}", lineNumber, e.InnerException.Message);
            }

            return response;
        }

        public void Dispose()
        {
            if (_httpServer != null) _httpServer.Dispose();
            if (_udpServer != null) _udpServer.Dispose();
        }
    }
}