using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace WSBridge {
    internal class Program {
        private const string ServerPrefix = "/WSBridge/Listener/";
        private const string ClientPrefix = "/WSBridge/Connect/";
        private static readonly object SyncObj = new object();
        private static readonly object _EchoExceptionSyncObj = new object();
        private static void echo(Exception e) { lock (_EchoExceptionSyncObj) Console.WriteLine(e); }
        private static void Main(string[] args) {
            var httpListener = new HttpListener();
            var addrs = httpListener.Prefixes;
            var port = 23333;
            foreach (var arg in args) {
                if (int.TryParse(arg, out var newPort)) {
                    if (newPort < 1024)
                        Console.WriteLine($"端口 {newPort} 太小，不被使用");
                    else if (newPort >= 65535)
                        Console.WriteLine($"端口 {newPort} 太大，不被使用");
                    else
                        port = newPort;
                } else {
                    addrs.Add($"http://{arg}:{port}{ServerPrefix}");
                    addrs.Add($"http://{arg}:{port}{ClientPrefix}");
                }
            }
            if (addrs.Count <= 0) {
                addrs.Add($"http://localhost:{port}{ServerPrefix}");
                addrs.Add($"http://localhost:{port}{ClientPrefix}");
            }
            try {
                Console.WriteLine("监听列表：");
                foreach (var addr in addrs)
                    Console.WriteLine(addr);
                httpListener.Start();
                Console.WriteLine("成功开始监听各入口");
            } catch (Exception e) {
                echo(e);
                Console.WriteLine("(非 localhost 的监听可能需要管理员权限)");
                return;
            }
            for (HttpListenerResponse response = null; ; response = null) {
                try {
                    var context = httpListener.GetContext();
                    response = context.Response;
                    var url = context.Request.Url.LocalPath;
                    if (!url.StartsWith(ServerPrefix) && !url.StartsWith(ClientPrefix)) {
                        response.StatusCode = 400;
                        response.Close();
                        continue;
                    }
                    var ska = context.AcceptWebSocketAsync(null);
                    if (!ska.Wait(10000))
                        response.Close();
                    else if (url.StartsWith(ServerPrefix))
                        StartServer(response, ska.Result.WebSocket, url.Substring(ServerPrefix.Length));
                    else if (url.StartsWith(ClientPrefix))
                        StartClient(response, ska.Result.WebSocket, url.Substring(ClientPrefix.Length));
                    else
                        response.Close();
                } catch (Exception e) {
                    echo(e);
                    if (response != null) try { response.Close(); } catch (Exception e2) { echo(e2); }
                    Thread.Sleep(333);
                    GC.Collect();
                }
            }
        }
        private const int mSaved = 1024;
        private static readonly HttpListenerResponse[] ClientResponses = new HttpListenerResponse[mSaved];
        private static readonly HttpListenerResponse[] ServerResponses = new HttpListenerResponse[mSaved];
        private static readonly WebSocket[] ClientWebSockets = new WebSocket[mSaved];
        private static readonly WebSocket[] ServerWebSockets = new WebSocket[mSaved];
        private static readonly ManualResetEvent[] hClient = new ManualResetEvent[mSaved];
        private static readonly Dictionary<string, int> ServerIndex = new Dictionary<string, int>();
        private static readonly Stack<int> FreeIndex = new Stack<int>(Range(mSaved));
        private static int[] Range(int n) {
            var result = new int[n];
            for (var i = 0; i < n; ++n) result[i] = i;
            return result;
        }
        private static void StartServer(HttpListenerResponse ServerResponse, WebSocket ServerWebSocket, string id) {
            var index = -1;
            lock (SyncObj) {
                if (ServerIndex.ContainsKey(id)) {
                    ServerWebSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "ID conflict", CancellationToken.None).Wait(3000);
                    ServerResponse.Close();
                    return;
                }
                if (FreeIndex.Count < 1) {
                    ServerWebSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Too Many", CancellationToken.None).Wait(3000);
                    ServerResponse.Close();
                    return;
                }
                ServerIndex.Add(id, index = FreeIndex.Pop());
                ServerResponses[index] = ServerResponse;
                ServerWebSockets[index] = ServerWebSocket;
                hClient[index] = new ManualResetEvent(false);
            }
            try {
                (new Thread(Delegate_RunningServer)).Start(index);
            } catch {
                if (index >= 0) {
                    lock (SyncObj) {
                        ClientResponses[index] = ServerResponses[index] = null;
                        ClientWebSockets[index] = ServerWebSockets[index] = null;
                        hClient[index] = null;
                        FreeIndex.Push(index);
                    }
                }
                throw;
            }
        }
        private static readonly ParameterizedThreadStart Delegate_RunningServer = RunningServer;
        private const int BufferSize = 65536;
        private static void RunningServer(object args) {
            if (!(args is int index)) return;
            var ServerResponse = ServerResponses[index];
            var ServerWebSocket = ServerWebSockets[index];
            HttpListenerResponse ClientResponse = null;
            WebSocket ClientWebSocket = null;
            using (var source = new CancellationTokenSource()) {
                try {
                    var token = source.Token;
                    //var ServerBuffer = new byte[BufferSize];
                    //var ClientBuffer = new byte[BufferSize];
                    hClient[index].WaitOne();
                    ClientResponse = ClientResponses[index];
                    ClientWebSocket = ClientWebSockets[index];
                    lock (SyncObj) {
                        ClientResponses[index] = ServerResponses[index] = null;
                        ClientWebSockets[index] = ServerWebSockets[index] = null;
                        hClient[index] = null;
                        FreeIndex.Push(index);
                    }
                    var Responses = new HttpListenerResponse[] { ServerResponse, ClientResponse };
                    var WebSockets = new WebSocket[] { ServerWebSocket, ClientWebSocket };
                    var Buffers = new byte[][] { new byte[BufferSize], new byte[BufferSize] };
                    //var WaittingReceiveFromServer = ServerWebSocket.ReceiveAsync(new ArraySegment<byte>(ServerBuffer), token);
                    //var WaittingReceiveFromClient = ClientWebSocket.ReceiveAsync(new ArraySegment<byte>(ClientBuffer), token);
                    var WaittingReceive = new Task<WebSocketReceiveResult>[] {
                        WebSockets[0].ReceiveAsync(new ArraySegment<byte>(Buffers[0]), token),
                        WebSockets[1].ReceiveAsync(new ArraySegment<byte>(Buffers[1]), token)
                    };
                    var Waitting = new Task[] { WaittingReceive[0], WaittingReceive[1] };
                    for (int src = Task.WaitAny(Waitting, token), dest; !token.IsCancellationRequested; src = Task.WaitAny(Waitting, token)) {
                        if (src < 0) return;
                        //else if (0 == i) {
                        //    if (WaittingReceiveFromServer == Waitting[0]) {
                        //        var info = WaittingReceiveFromServer.Result;
                        //        if (info.CloseStatus is WebSocketCloseStatus webSocketCloseStatus) {
                        //            ClientWebSocket.CloseAsync(webSocketCloseStatus, info.CloseStatusDescription, CancellationToken.None).Wait(3000);
                        //            return;
                        //        }
                        //        Waitting[0] = ClientWebSocket.SendAsync(new ArraySegment<byte>(ServerBuffer, 0, info.Count), info.MessageType, info.EndOfMessage, token);
                        //        WaittingReceiveFromServer.Dispose();
                        //        WaittingReceiveFromServer = null;
                        //    } else {
                        //        Waitting[0].Dispose();
                        //        Waitting[0] = WaittingReceiveFromServer = ServerWebSocket.ReceiveAsync(new ArraySegment<byte>(ServerBuffer), token);
                        //    }
                        //} else if (1 == i) {
                        if (0 == src || 1 == src) {
                            dest = src ^ 1;
                            if (WaittingReceive[src] == Waitting[src]) {
                                var info = WaittingReceive[src].Result;
                                if (info.CloseStatus is WebSocketCloseStatus webSocketCloseStatus) {
                                    WebSockets[dest].CloseAsync(webSocketCloseStatus, info.CloseStatusDescription, CancellationToken.None).Wait(3000);
                                    return;
                                }
                                Waitting[src] = WebSockets[dest].SendAsync(new ArraySegment<byte>(Buffers[src], 0, info.Count), info.MessageType, info.EndOfMessage, token);
                                WaittingReceive[src].Dispose();
                                WaittingReceive[src] = null;
                            } else {
                                Waitting[src].Dispose();
                                Waitting[src] = WaittingReceive[src] = WebSockets[src].ReceiveAsync(new ArraySegment<byte>(Buffers[src]), token);
                            }
                        } else throw new Exception("[Error 1]");
                    }
                } catch (Exception e) {
                    echo(e);
                    try { source.Cancel(); } catch { }
                    if (null != ServerWebSocket) try { ServerWebSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, e.Message, CancellationToken.None).Wait(3000); } catch { }
                    if (null != ClientWebSocket) try { ClientWebSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, e.Message, CancellationToken.None).Wait(3000); } catch { }
                } finally {
                    if (null != ServerResponse) try { ServerResponse.Close(); } catch { }
                    if (null != ClientResponse) try { ClientResponse.Close(); } catch { }
                }
            }
        }
        private static void StartClient(HttpListenerResponse ClientResponse, WebSocket ClientWebSocket, string id) {
            lock (SyncObj) {
                if (ServerIndex.TryGetValue(id, out var index)) {
                    ServerIndex.Remove(id);
                    ClientResponses[index] = ClientResponse;
                    ClientWebSockets[index] = ClientWebSocket;
                    hClient[index].Set();
                } else {
                    ClientWebSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "The Server does Not Exist", CancellationToken.None).Wait(3000);
                    ClientResponse.Close();
                }
            }
        }
    }
}
