
using System.Net;
using System.Text;

namespace AsyncServer
{
    public class Program
    {
        static volatile bool end = false;
        static Queue<HttpListenerContext> queue = new Queue<HttpListenerContext>();
        static object lockObj = new object();
        static Logger logger = new Logger();
        static ProxyServer server = new ProxyServer(200, logger);
        static SemaphoreSlim sem = new SemaphoreSlim(0);
        static HttpListener listener = new HttpListener();

        static void Main()
        {
            ThreadPool.SetMaxThreads(200, 200);
            logger.ChangeLogFile("console");
            listener.Prefixes.Add("http://localhost:5000/");
            listener.Start();
            Thread sd_th = new Thread(_ => ShutdownThread());
            Thread cl_th = new Thread(_ => ClearingThread());
            sd_th.Start();
            cl_th.Start();


            while (!end)
            {
                try
                {
                    HttpListenerContext context = listener.GetContext();
                    lock (lockObj)
                    {
                        queue.Enqueue(context);
                    }
                    sem.Release();
                    Task.Run(() => ProcessingThreadAsync());
                }
                catch (Exception ex)
                {
                    logger.Log("Server is offline");
                }
            }
        }


        static async Task ProcessingThreadAsync()
        {
            try
            {
                await sem.WaitAsync();

                HttpListenerContext context;
                lock (lockObj)
                {
                    if (end && queue.Count == 0)
                        return;
                    context = queue.Dequeue();
                }

                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;
                string param = request.Url.Query;
                string responseText;

                List<long> ids = await server.RequestAsync(param)
                    .ContinueWith(t =>
                    {
                        logger.Log($"RequestAsync completed, returned {t.Result.Count} ids");
                        return t.Result;
                    });

                if (ids.Count == 0)
                {
                    responseText = "<li>No results found</li>";
                    logger.Log("Empty list returned");
                }
                else
                {
                    List<Task<string>> tasks = new List<Task<string>>();
                    foreach (long id in ids)
                    {
                        Task<string> t = server.GetPicURLAsync(id);
                        tasks.Add(t);
                    }

                    string[] results = await Task.WhenAll(tasks)
                        .ContinueWith(t =>
                        {
                            logger.Log($"WhenAll completed, fetched {t.Result.Length} urls");
                            return t.Result;
                        });

                    List<string> urls = new List<string>();
                    foreach (string u in results)
                    {
                        if (!string.IsNullOrEmpty(u))
                            urls.Add(u);
                    }

                    List<string> liItems = new List<string>();
                    foreach (string u in urls)
                    {
                        liItems.Add($"<li><a href='{u}'>{u}</a></li>");
                    }
                    responseText = string.Join("\n", liItems);
                }

                byte[] buffer = Encoding.UTF8.GetBytes($"<html><body><ul>{responseText}</ul></body></html>");
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                logger.Log($"ERROR in processing thread: {ex}");
            }
        }
        static void ShutdownThread()
        {

            Console.WriteLine("Press 67 for shutdown\n");

            while (true)
            {
                string input = Console.ReadLine();
                if (input == "67")
                {
                    end = true;
                    logger.Log("Shutting down server");
                    listener.Stop();
                    logger.Write();
                    logger.CloseStream();
                    break;
                }
            }
        }

        static void ClearingThread()
        {
            while (!end)
            {
                Thread.Sleep(10000);
                server.ClearServer();
            }
        }
    }
}

