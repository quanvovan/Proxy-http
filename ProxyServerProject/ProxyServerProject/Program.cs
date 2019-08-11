using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ProxyServerProject
{
    class Program
    {
        static void Main(string[] args)
        {
            ProxyServer proxyServer = ProxyServer.GetInstance();
            proxyServer.Start();
            while (Console.ReadKey().Key!=ConsoleKey.Escape)
            {

            }
            //Save cache file
            string path = AppDomain.CurrentDomain.BaseDirectory + "Cache\\CacheDictionary.csv";
            File.WriteAllLines(path, ProxyServer.cacheDict.Select(x => String.Join(",", x.Key, x.Value.ToString())));
        }
    }

    class ProxyServer
    {
        private static ProxyServer _instance;

        private ProxyServer()
        {

        }

        public static ProxyServer GetInstance()
        {
            if (_instance == null)
            {
                _instance = new ProxyServer();
            }
            return _instance;
        }

        //Khai bao cac hang so
        const int proxyPort = 8888;
        const string proxyIP = "127.0.0.1";
        const string blacklistFile = "blacklist.conf";
        const int httpPort = 80;
        const int bufferSize = 4096;
        const int maxConn = 100;
        const string forbiddenStatus = "HTTP/1.1 403 Forbidden\r\n\r\n"
            + "<!DOCTYPE HTML PUBLIC \"-//IETF//DTD HTML 2.0//EN\">\r\n"
            + "<html><head>\r\n"
            + "<title>403 Forbidden</title>\r\n"
            + "</head><body>\r\n"
            + "<h1>403 Forbidden</h1>\r\n"
            + "<p>You don't have permission to access /forbidden/\r\n"
            + "on this server.</p>\r\n"
            + "</body></html>\r\n";
        object outLock = new object();
        object cacheLock = new object();

        public static Dictionary<string, int> cacheDict = new Dictionary<string, int>();
        //0: not done
        //1: done

        void SocketListener()
        {
            try
            {
                IPAddress ipAddr = IPAddress.Parse(proxyIP);
                IPEndPoint localEndPoint = new IPEndPoint(ipAddr, proxyPort);

                //Start listening
                TcpListener listener = new TcpListener(localEndPoint);
                listener.Start(maxConn);

                Console.WriteLine("Proxy Server: {0}", listener.LocalEndpoint);
                Console.WriteLine("Press ESC to exit\n");

                while (true)
                {
                    Socket clientSocket = listener.AcceptSocket();
                
                    Thread clientThread = new Thread(ClientToProxy);
                    clientThread.IsBackground = true;
                    clientThread.Start(clientSocket);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex);
            }
        }

        void ClientToProxy(object obj)
        {
            Socket clientSocket = (Socket)obj;
            byte[] data = new byte[bufferSize];

            try
            {
                clientSocket.Receive(data);

                ProxyToServer(clientSocket, data);
                clientSocket.Close();
            }
            catch (Exception ex)
            {
                lock(outLock)
                {
                    Console.WriteLine("Error: " + ex);
                    Console.WriteLine("Error: {0}", clientSocket.RemoteEndPoint);
                    Console.WriteLine(Encoding.ASCII.GetString(data));
                    Console.WriteLine("-----------------------------------------------------------------------\n");
                }

                clientSocket.Close();
            }
        }

        void ProxyToServer(Socket clientSocket, byte[] data)
        {
            //Check GET POST
            string firstline = Encoding.ASCII.GetString(data).Split('\n')[0];
            if (!(firstline.Contains("GET") || firstline.Contains("POST")))
                return;

            string host = "";
            int port = -1;
            GetHostPort(Encoding.ASCII.GetString(data), out host, out port);
            //Console.WriteLine("Connection from {0} to {1}:{2}\n{3}\n", clientSocket.RemoteEndPoint, host, port, firstline);
            

            if (IsAllow(host))
            {
                string cacheName = Sha256Hash(firstline);
                string cachePath = AppDomain.CurrentDomain.BaseDirectory + "Cache\\" + "cache_" + cacheName + ".dat";

                //Check cache
                bool canLoadCache = false;
                bool canSaveCache = false;
                lock(cacheLock)
                {
                    if (cacheDict.ContainsKey(cacheName))
                    {
                        canSaveCache = false;
                        if (cacheDict[cacheName] == 0)
                            canLoadCache = false;
                        else
                            canLoadCache = true;
                    }
                    else
                    {
                        canLoadCache = false;
                        canSaveCache = true;
                        cacheDict.Add(cacheName, 0);
                    }
                }

                if (canLoadCache)
                {
                    try
                    {
                        clientSocket.Send(File.ReadAllBytes(cachePath));
                        lock(outLock)
                        {
                            Console.WriteLine("Connection from {0} to {1}:{2}\n{3}\n", clientSocket.RemoteEndPoint, host, port, firstline);
                            Console.WriteLine("Load {0} to {1}\n", "cache_" + cacheName + ".dat", clientSocket.RemoteEndPoint);
                            Console.WriteLine("-----------------------------------------------------------------------\n");
                        }
                    }
                    catch
                    {
                        canLoadCache = false;
                    }
                }

                if (!canLoadCache)
                {
                    TcpClient client = new TcpClient();
                    if (host.All<char>(x => char.IsDigit(x) || x == '.'))
                    {
                        client.Connect(IPAddress.Parse(host), port);
                    }
                    else
                    {
                        client.Connect(host, port);
                    }
                    var stream = client.GetStream();

                    stream.Write(data, 0, data.Length);
                    
                    byte[] recvBytes = new byte[bufferSize];
                    int total = 0;

                    if (canSaveCache)
                    {
                        FileStream cache = new FileStream(cachePath, FileMode.Create);

                        try
                        {
                            while (true)
                            {
                                int rBytes = stream.Read(recvBytes, 0, recvBytes.Length);
                                if (rBytes > 0)
                                {
                                    clientSocket.Send(recvBytes, 0, rBytes, SocketFlags.None);
                                    cache.Write(recvBytes, 0, rBytes);
                                    total += rBytes;
                                }
                                else break;
                            }

                            cache.Close();
                            lock (cacheLock)
                            {
                                cacheDict[cacheName] = 1;
                            }
                        }
                        catch (Exception ex)
                        {
                            cache.Close();
                            lock (cacheLock)
                            {
                                cacheDict.Remove(cacheName);
                            }
                            Console.WriteLine(ex);
                        }

                    }
                    else
                    {
                        try
                        {
                            while (true)
                            {
                                int rBytes = stream.Read(recvBytes, 0, recvBytes.Length);
                                if (rBytes > 0)
                                {
                                    clientSocket.Send(recvBytes, 0, rBytes, SocketFlags.None);
                                    total += rBytes;
                                }
                                else break;
                            }
                        }
                        catch
                        {
                            //Console.WriteLine("Receive Error");
                        }
                    }

                    client.Close();
                    //Console.WriteLine("Received from {0}:{1} to {2}: {3} bytes\n", host, port, clientSocket.RemoteEndPoint, total);
                    lock (outLock)
                    {
                        Console.WriteLine("Connection from {0} to {1}:{2}\n{3}\n", clientSocket.RemoteEndPoint, host, port, firstline);
                        Console.WriteLine("Received from {0}:{1} to {2}: {3} bytes\n", host, port, clientSocket.RemoteEndPoint, total);
                        Console.WriteLine("-----------------------------------------------------------------------\n");
                    }
                }
                
            }
            else
            {
                clientSocket.Send(Encoding.ASCII.GetBytes(forbiddenStatus));
            }
        }

        void GetHostPort(string data, out string host, out int port)
        {
            string firstline = data.Split('\n')[0];
            string url = firstline.Split(' ')[1];

            int http_pos = url.IndexOf("://");
            string temp = url;
            if (http_pos != -1)
            {
                temp = url.Substring(http_pos + 3);
            }
            int portPos = temp.IndexOf(":");
            int hostPos = temp.IndexOf("/");
            if (hostPos == -1)
                hostPos = temp.Length;

            if (portPos == -1 || hostPos < portPos)
            {
                port = httpPort;
                host = temp.Substring(0, hostPos);
            }
            else
            {
                port = Int32.Parse(temp.Substring(portPos + 1, hostPos - portPos - 1));
                host = temp.Substring(0, portPos);
            }
        }

        bool IsAllow(string host)
        {
            if (host!=null)
            {
                try
                {
                    using (StreamReader file = new StreamReader(blacklistFile))
                    {
                        string line = file.ReadLine();
                        while (line != null)
                        {
                            line = line.Replace("\r", string.Empty).Replace("\n", string.Empty);
                            //Transfrom www.abc.com to abc.com
                            if (line.Length>4)
                            {
                                if (line.Substring(0, 4) == "www.")
                                    line = line.Substring(4);
                            }
                            if (host.Length>4)
                            {
                                if (host.Substring(0, 4) == "www.")
                                    host = host.Substring(4);
                            }

                            if (host.Contains(line))
                                return false;
                            line = file.ReadLine();
                        }
                    }
                }
                catch
                {
                    Console.WriteLine("Cannot open blacklist.config");
                }
            }

            return true;
        }

        string Sha256Hash(string data)
        {
            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(data));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }

        public void Start()
        {
            //Load cache file
            string path = AppDomain.CurrentDomain.BaseDirectory + "Cache\\CacheDictionary.csv";
            if (File.Exists(path))
            {
                cacheDict = File.ReadLines(path)
                    .Select(line => line.Split(','))
                    .ToDictionary(split => split[0], split => Int32.Parse(split[1]));
            }

            Thread clientThread = new Thread(SocketListener);
            clientThread.IsBackground = true;
            clientThread.Start();
        }
    }
}
