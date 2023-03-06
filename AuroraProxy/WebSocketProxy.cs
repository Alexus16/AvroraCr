﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace AuroraProxy
{
    public class WebSocketProxy
    {
        private readonly string newLine = "\r\n";

        private Socket _acceptingSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
        private Socket _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
        private Socket _clientSocket;

        private NetworkStream _clientStream;
        private NetworkStream _serverStream;
        private SslStream _securedServerStream;

        private string _serverHost;
        private int _serverPort;
        private int _proxyPort;

        public bool IsServerConnectionInitialized { get; private set; } = false;
        public bool IsClientConnectionInitialized { get; private set; } = false;
        public string CustomUserAgent { get; set; }

        public WebSocketProxy(string serverHost, int serverPort, int proxyPort)
        {
            _serverHost = serverHost;
            _serverPort = serverPort;
            _proxyPort = proxyPort;
        }

        public void Init()
        {
            _acceptingSocket.Bind(new IPEndPoint(IPAddress.Loopback, _proxyPort));
            _acceptingSocket.Listen(128);
            _acceptingSocket.BeginAccept(onClientAccepted, null);
        }

        private void onClientAccepted(IAsyncResult result)
        {
            WebSocketFrameCoder clientCoder = new WebSocketFrameCoder();
#if DEBUG
            Console.WriteLine("Initializing proxy session started.");
            Console.WriteLine("Connecting PROXY <-> CLIENT");
#endif
            _clientSocket = _acceptingSocket.EndAccept(result);
#if DEBUG
            Console.WriteLine("Connected PROXY <-> CLIENT");
#endif
            _clientStream = new NetworkStream(_clientSocket);
#if DEBUG
            Console.WriteLine("Checking SERVER <-> PROXY connection");
#endif
            if(!_serverSocket.Connected)
            {
#if DEBUG
                Console.WriteLine("Connecting SERVER <-> PROXY");
#endif
                _serverSocket.BeginConnect(_serverHost, _serverPort, onServerConnected, null);
            }
            handshakeClient(_clientStream);
            while (!IsServerConnectionInitialized) { }
            IsClientConnectionInitialized = true;
            while (_clientStream.Socket.Connected)
            {
                if (_clientStream.DataAvailable)
                {
                    List<byte> fullReadBuffer = new List<byte>();
                    while (_clientStream.DataAvailable)
                    {
                        byte[] readBuffer = new byte[1024*1024];
                        int len = _clientStream.Read(readBuffer, 0, readBuffer.Length);
                        fullReadBuffer.AddRange(readBuffer.Take(len));
                    }
                    WebSocketFrame[] readFrames = clientCoder.FromBytes(fullReadBuffer.ToArray());
                    string[] texts = clientCoder.DecodeTextData(readFrames.Where(f => f.OpCode == WebSocketFrameOpCode.TEXT).ToArray());
                    foreach (string text in texts)
                    {
                        Console.WriteLine($"CLIENT -> SERVER: {text}");
                        byte[] retranslateBuffer = clientCoder.ToBytes(clientCoder.EncodeTextData(text, true));
                        _securedServerStream.Write(retranslateBuffer, 0, retranslateBuffer.Length);
                    }
                }
            }
        }

        private void onServerConnected(IAsyncResult result)
        {
            WebSocketFrameCoder serverCoder = new WebSocketFrameCoder();
#if DEBUG
            Console.WriteLine("Connected SERVER <-> PROXY");
#endif
            _serverStream = new NetworkStream(_serverSocket);
            _securedServerStream = new SslStream(_serverStream);
#if DEBUG
            Console.WriteLine("SSL Auth SERVER <-> PROXY");
#endif
            _securedServerStream.AuthenticateAsClient(_serverHost);
#if DEBUG
            Console.WriteLine("SSL Authed SERVER <-> PROXY");
#endif
            handshakeServer(_securedServerStream, _serverStream);
            IsServerConnectionInitialized = true;
            while(!IsClientConnectionInitialized) { }
            while(_serverStream.Socket.Connected)
            {
                if(_serverStream.DataAvailable)
                {
                    List<byte> fullReadBuffer = new List<byte>();
                    while (_serverStream.DataAvailable)
                    {
                        byte[] readBuffer = new byte[1024*1024];
                        int len = _securedServerStream.Read(readBuffer, 0, readBuffer.Length);
                        fullReadBuffer.AddRange(readBuffer.Take(len));
                    }
                    WebSocketFrame[] readFrames = serverCoder.FromBytes(fullReadBuffer.ToArray());
                    if(readFrames.Length == 0) continue;
                    WebSocketFrame pingFrame = readFrames.Where(f => f.OpCode == WebSocketFrameOpCode.PING).LastOrDefault();
                    if(pingFrame is not null)
                    {
                        Console.WriteLine("PROXY <-> SERVER: Ping-Pong");
                        WebSocketFrame[] frames = { serverCoder.CreatePongFrame(pingFrame) };
                        byte[] pongBuffer = serverCoder.ToBytes(frames);
                        _securedServerStream.Write(pongBuffer);
                    }
                    string[] texts = serverCoder.DecodeTextData(readFrames.Where(f => f.OpCode == WebSocketFrameOpCode.TEXT).ToArray());
                    foreach (string text in texts)
                    {
                        Console.WriteLine($"SERVER -> CLIENT: {text}");
                        var frames = serverCoder.EncodeTextData(text, false);
                        byte[] retranslateBuffer = serverCoder.ToBytes(frames);
                        _clientStream.Write(retranslateBuffer, 0, retranslateBuffer.Length);
                    }
                }
            }
#if DEBUG
            Console.WriteLine("Disconnected SERVER <-> PROXY");
#endif
        }

        private void handshakeServer(SslStream securedStream, NetworkStream stream)
        {
#if DEBUG
            Console.WriteLine("Handshaking server started");
#endif
            string handshakeRequest = createServerHandshake(_serverHost);
#if DEBUG
            Console.WriteLine($"Server Handshake Request:\n{handshakeRequest}\n");
#endif
            byte[] buffer = Encoding.UTF8.GetBytes(handshakeRequest);
            securedStream.Write(buffer);
            while (!stream.DataAvailable) { }
            StringBuilder sb = new StringBuilder();
            while (stream.DataAvailable)
            {
                byte[] readBuffer = new byte[10240];
                int len = securedStream.Read(readBuffer, 0, readBuffer.Length);
                sb.Append(Encoding.UTF8.GetString(readBuffer, 0, len));
            }
            string response = sb.ToString();
#if DEBUG
            Console.WriteLine($"Server Handshake Response:\n{response}\n");
            if (response.StartsWith("HTTP/1.1 101")) Console.WriteLine("Handshake complete");
#endif
        }

        private void handshakeClient(NetworkStream stream)
        {
#if DEBUG
            Console.WriteLine("Handshaking client started");
#endif
            while(!stream.DataAvailable) { }
            StringBuilder sb = new StringBuilder();
            while (stream.DataAvailable)
            {
                byte[] readBuffer = new byte[1024];
                int len = stream.Read(readBuffer, 0, readBuffer.Length);
                sb.Append(Encoding.UTF8.GetString(readBuffer, 0, len));
            }
            string request = sb.ToString();
#if DEBUG
            Console.WriteLine($"Client handshake request:\n{request}\n");
#endif
            string response = createClientHandshakeResponse(request);
#if DEBUG
            Console.WriteLine($"Client handshake response:\n{response}\n");
#endif
            byte[] buffer = Encoding.UTF8.GetBytes(response);
            stream.Write(buffer);
#if DEBUG
            Console.WriteLine("Handshaking client complete");
#endif
        }

        private string createServerHandshake(string host)
        {
            Random random = new Random();
            byte[] buffer = new byte[16];
            random.NextBytes(buffer);
            StringBuilder requestSB = new StringBuilder();
            requestSB.AppendLine("GET /student/arm/ HTTP/1.1");
            requestSB.AppendLine("Host: " + host);
            requestSB.AppendLine("Connection: Upgrade");
            requestSB.AppendLine("Pragma: no-cache");
            requestSB.AppendLine("Cache-control: no-cache");
            requestSB.AppendLine("User-Agent: " + CustomUserAgent);
            requestSB.AppendLine("Upgrade: websocket");
            requestSB.AppendLine("Origin: https://" + host);
            requestSB.AppendLine("Sec-WebSocket-Version: 13");
            requestSB.AppendLine("Accept-Encoding: gzip, deflate, br");
            requestSB.AppendLine("Sec-WebSocket-Key: " + Convert.ToBase64String(buffer));
            requestSB.AppendLine("Sec-WebSocket-Extensions: permessage-deflate; client_max_window_bits");
            requestSB.AppendLine();
            return requestSB.ToString();
        }

        private string createClientHandshakeResponse(string handshakeRequest)
        {
            string guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            StringBuilder responseSB = new StringBuilder();
            responseSB.AppendLine("HTTP/1.1 101 Switching Protocols");
            responseSB.AppendLine("Upgrade: websocket");
            responseSB.AppendLine("Connection: Upgrade");
            string[] requestLines = handshakeRequest.Split(newLine);
            Dictionary<string, string> headerKeys = new Dictionary<string, string>();
            foreach (var line in requestLines[1..])
            {
                string[] parts = line.Split(": ");
                if (parts.Length < 2) continue;
                headerKeys.Add(parts[0], parts[1..].Aggregate((a, b) => a + b).Trim());
            }
            string hash = headerKeys["Sec-WebSocket-Key"] + guid;
            hash = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(hash)));
            responseSB.AppendLine("Sec-WebSocket-Accept: " + hash);
            responseSB.AppendLine();
            return responseSB.ToString();
        }
    }
}
