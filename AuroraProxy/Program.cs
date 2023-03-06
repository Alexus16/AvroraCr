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

WebSocketProxy proxy = new WebSocketProxy("mirea.aco-avrora.ru", 443, 500);
proxy.Init();

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