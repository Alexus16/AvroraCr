using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Security;
using System.Security.Cryptography;
using WebSocket4Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using AuroraProxy;

HttpListener listener = new HttpListener();

listener.Realm = "mirea.aco-avrora.ru";
listener.Prefixes.Add("http://127.0.0.1/");
listener.Start();
bool isAuth = false;

string originalUrl = "https://mirea.aco-avrora.ru";
string originalUrlWS = "mirea.aco-avrora.ru";
string avroraUserAgent = "Mozilla/5.0 (Windows NT 6.2; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) QtWebEngine/5.14.2 Chrome/77.0.3865.129 Safari/537.36";
string newLine = "\r\n";

const string startPageLocalPath = "/";
const string studentPageLocalPath = "/student/";

Thread thread = new Thread(() =>
{
    while (true)
    {
        var context = listener.GetContext();
        var request = context.Request;
        var response = context.Response;
        string pathAndQuery = request.Url.PathAndQuery;
        //Console.WriteLine($"Req started: {request.Url.ToString()}");
        if (!pathAndQuery.Contains("student"))
        {
            pathAndQuery = "/student" + pathAndQuery;
        }
        var originalPage = getOriginalPageText(pathAndQuery);
        if(request.Url.LocalPath == startPageLocalPath || request.Url.LocalPath == studentPageLocalPath)
        {
            originalPage = originalPage.Replace("+location.host+'/student/arm/'", "+\"127.0.0.1:500\"");
            originalPage = originalPage.Replace("wss:", "ws:");
        }
        context.Response.OutputStream.Write(Encoding.UTF8.GetBytes(originalPage));
        response.OutputStream.Close();
        response.Close();
        //Console.WriteLine($"Req finished: {pathAndQuery}");
    }
});
thread.Start();

Socket serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
Socket clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
Socket acceptedClientSocket;
SslStream serverSslStream = null;

clientSocket.Bind(new IPEndPoint(IPAddress.Loopback, 500));
clientSocket.Listen(128);
clientSocket.BeginAccept(OnClientAccepted, null);


void OnClientAccepted(IAsyncResult result)
{
    Console.WriteLine("Try to connect socket");
    try
    {
        acceptedClientSocket = clientSocket.EndAccept(result);
        Console.WriteLine("Socket client<->proxy connected");
        if (!serverSocket.Connected)
        {
            Console.WriteLine("Socket server<->proxy connecting...");
            serverSocket.BeginConnect(originalUrlWS, 443, OnServerConnected, null);
            //ClientWebSocket socket = new ClientWebSocket();
            //CancellationTokenSource source = new CancellationTokenSource();
            //socket.ConnectAsync(new Uri("wss://mirea.aco-avrora.ru/student/arm/"), source.Token).Wait();
        }
        var networkStream = new NetworkStream(acceptedClientSocket);
        while (acceptedClientSocket.Connected)
        {
            if (networkStream.DataAvailable)
            {
                List<byte> fullmessageBuffer = new List<byte>();
                StringBuilder builder = new StringBuilder();
                while(networkStream.DataAvailable)
                {
                    byte[] buffer = new byte[1024 * 30];
                    int len = networkStream.Read(buffer, 0, buffer.Length);
                    builder.Append(Encoding.UTF8.GetString(buffer, 0, len));
                    fullmessageBuffer.AddRange(buffer.Take(len));
                    Thread.Sleep(1);
                }
                string request = builder.ToString();
                //Console.WriteLine($"Client -> Proxy:\n{request}\n=-=End message=-=");
                if (checkHandshakeHTTPRequest(request))
                {
                    string handshakeAnswer = createAnswerWebSocketHandshake(request);
                    Console.WriteLine($"Handshake request detected, answer:\n{handshakeAnswer}\n=-=End answer=-=");
                    acceptedClientSocket.Send(Encoding.UTF8.GetBytes(handshakeAnswer));
                }
                else
                {
                    string[] requests = WebSocketFrameDecoder.Decode(fullmessageBuffer.ToArray());
                    foreach(var req in requests)
                    {
                        Console.WriteLine($"Client -> Proxy(exp):\n{req}\n=-=End Message=-=");
                        while ((!serverSocket?.Connected ?? true) || !isAuth) { }
                        byte[] buffer = WebSocketFrameDecoder.Encode(req, true);
                        Console.WriteLine($"Proxy -> Server:\n{WebSocketFrameDecoder.Decode(buffer)[0]}\n=-=End Message=-=");
                        serverSslStream.Write(buffer);
                    }
                    //Console.WriteLine(WebSocketFrameDecoder.Decode(buffer));
                    //serverSocket.Send(fullmessageBuffer.ToArray());
                }
            }
        }
        serverSocket?.Disconnect(false);
    }
    catch
    {
        throw;
    }
    finally
    {
        clientSocket.BeginAccept(OnClientAccepted, null);
    }
}

void OnServerConnected(IAsyncResult result)
{
    var networkStream = new NetworkStream(serverSocket);
    serverSslStream = new SslStream(networkStream, false, new RemoteCertificateValidationCallback(checkServerCert), null);
    serverSslStream.AuthenticateAsClient("mirea.aco-avrora.ru");
    //serverSocket.EndConnect(result);
    while (!serverSocket.Connected) { }
    Console.WriteLine("Socket server<->proxy connected");
    if(serverSocket.Connected)
    {
        string handshakeRequest = createRequestWebSocketHandshake("mirea.aco-avrora.ru");
        Console.WriteLine($"Handshaking server:\n{handshakeRequest}\n=-=End request=-=");
        serverSslStream.Write(Encoding.UTF8.GetBytes(handshakeRequest));
    }
    isAuth = true;
    while(serverSocket.Connected)
    {
        if(networkStream.DataAvailable)
        {
            StringBuilder builder = new StringBuilder();
            List<byte> fullmessageBuffer = new List<byte>();
            while(networkStream.DataAvailable)
            {
                byte[] buffer = new byte[1024 * 30];
                int len = serverSslStream.Read(buffer, 0, buffer.Length);
                builder.Append(Encoding.UTF8.GetString(buffer, 0, len));
                fullmessageBuffer.AddRange(buffer.Take(len));
            }
            string rawRequest = builder.ToString();
            if(!rawRequest.StartsWith("HTTP"))
            {
                //Console.WriteLine("Received something");
                byte[] pongAnswer = WebSocketFrameDecoder.CreatePong(fullmessageBuffer.ToArray());
                if (pongAnswer is not null)
                {
                    Console.WriteLine("Ping received, pong sending...");
                    serverSslStream.Write(pongAnswer);
                }
                string[] requests = WebSocketFrameDecoder.Decode(fullmessageBuffer.ToArray());
                foreach(var req in requests)
                {
                    Console.WriteLine($"Server -> Proxy:\n{req}\n=-=End Message=-=");
                    byte[] buffer = WebSocketFrameDecoder.Encode(req, false);
                    Console.WriteLine($"Proxy -> Client:\n{WebSocketFrameDecoder.Decode(buffer)[0]}\n=-=End Message=-=");
                    acceptedClientSocket.Send(buffer);
                }
        }
        }
    }
    Console.WriteLine("Socket server<->proxy disconnected");
}

bool checkServerCert(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
{
    return true;
}


string getOriginalPageText(string localPath)
{
    //Console.WriteLine($"Page requested: {localPath}");
    HttpClient copyClient = new HttpClient();
    copyClient.DefaultRequestHeaders.Add("User-Agent", avroraUserAgent);
    int errorCounter = 0;
    while(true)
    {
        try
        {
            var originalResponse = copyClient.GetAsync(originalUrl + localPath).Result;
            var originalPageText = originalResponse.Content.ReadAsStringAsync().Result;
            //Console.WriteLine($"Page received: {localPath}");
            return originalPageText;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Page receiving error: {ex.Message}");
            if(errorCounter++ > 5)
            {
                return ex.Message;
            }    
        }
    }
}


bool checkHandshakeHTTPRequest(string httpRequest)
{
    string[] requestLines = httpRequest.Split(newLine);
    Regex httpStartRegex = new Regex("GET (.*) HTTP/1.1");
    if (!httpStartRegex.IsMatch(requestLines[0])) return false;
    return true;
}

string createAnswerWebSocketHandshake(string handshakeRequest)
{
    string guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    StringBuilder responseSB = new StringBuilder();
    responseSB.AppendLine("HTTP/1.1 101 Switching Protocols");
    responseSB.AppendLine("Upgrade: websocket");
    responseSB.AppendLine("Connection: Upgrade");
    string[] requestLines = handshakeRequest.Split(newLine);
    Dictionary<string, string> headerKeys = new Dictionary<string, string>();
    foreach(var line in requestLines[1..])
    {
        string[] parts = line.Split(": ");
        if(parts.Length < 2) continue;
        headerKeys.Add(parts[0], parts[1..].Aggregate((a,b) => a + b).Trim());
    }
    string hash = headerKeys["Sec-WebSocket-Key"] + guid;
    hash = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(hash)));
    responseSB.AppendLine("Sec-WebSocket-Accept: " + hash);
    responseSB.AppendLine();
    return responseSB.ToString();
}

string createRequestWebSocketHandshake(string host)
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
    requestSB.AppendLine("User-Agent: " + avroraUserAgent);
    requestSB.AppendLine("Upgrade: websocket");
    requestSB.AppendLine("Origin: https://" + host);
    requestSB.AppendLine("Sec-WebSocket-Version: 13");
    requestSB.AppendLine("Accept-Encoding: gzip, deflate, br");
    //requestSB.AppendLine("Accept-Language: ru-RU,ru;q=0.8");
    requestSB.AppendLine("Sec-WebSocket-Key: " + Convert.ToBase64String(buffer));
    requestSB.AppendLine("Sec-WebSocket-Extensions: permessage-deflate; client_max_window_bits");
    requestSB.AppendLine();
    return requestSB.ToString();
}