using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AsyncServer
{
    internal class ProxyServer
    {
        private readonly Cache cache;
        private readonly Logger logger;
        private static readonly HttpClient client = new HttpClient();
        private static readonly ConcurrentDictionary<long, SemaphoreSlim> sems = new ConcurrentDictionary<long, SemaphoreSlim>();

        public ProxyServer(int cacheSize = 100, Logger logger = null)
        {
            this.cache = new Cache(cacheSize, logger);
            this.logger = logger ?? new Logger();
        }

        public async Task<List<long>> RequestAsync(string param)
        {
            if (string.IsNullOrWhiteSpace(param))
            {
                return new List<long>();
            }

            string url = $"https://data.rijksmuseum.nl/search/collection{param}";
            try
            {
                HttpResponseMessage resp = await client.GetAsync(url);

                resp.EnsureSuccessStatusCode();
                logger.Log("Response from Rijks was OK");

                string content = await resp.Content.ReadAsStringAsync();

                if (string.IsNullOrWhiteSpace(content))
                {
                    logger.Log("Empty response body from API");
                    return new List<long>();
                }

                using JsonDocument doc = JsonDocument.Parse(content);


                JsonElement root = doc.RootElement;
                JsonElement items = root.GetProperty("orderedItems");

                List<long> ids = new List<long>();

                foreach (JsonElement element in items.EnumerateArray())
                {
                    string link = element.GetProperty("id").GetString() ?? "";
                    string lastPart = link.Split("/").Last();

                    if (long.TryParse(lastPart, out long id))
                    {
                        ids.Add(id);
                    }
                    else
                    {
                        logger.Log($"Could not extract id from {link}");
                    }
                }
                return ids;
            }
            catch (Exception ex)
            {
                logger.Log($"ERROR! in method Request(string {param}): " + ex.ToString());
                return new List<long>();
            }

        }

        public async Task<string> GetPicURLAsync(long id)
        {
            string picURL = cache.Get(id);

            if (!string.IsNullOrEmpty(picURL))
                return picURL;

            SemaphoreSlim sem = sems.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

            try
            {
                await sem.WaitAsync();

                picURL = cache.Get(id);
                if (!string.IsNullOrEmpty(picURL))
                {
                    return picURL;
                }

                string searchURL = "https://data.rijksmuseum.nl/" + id.ToString();

                HttpResponseMessage resp = await client.GetAsync(searchURL);

                picURL = resp.RequestMessage?.RequestUri?.ToString() ?? string.Empty;

                if (resp.IsSuccessStatusCode)
                {
                    cache.Add(id, picURL);
                    return picURL;
                }

                logger.Log($"For id:{id} no link could be resloved");


                return string.Empty;
            }
            finally
            {
                sem.Release();
                sems.TryRemove(id, out _);
            }

        }
 
        public void ClearServer()
        {
            ThreadPool.GetMaxThreads(out int max, out _);
            ThreadPool.GetAvailableThreads(out int free, out _);

            if (max == free) // ovo je samo radi testiranja, kod kompleksnih sistema ovde bi se pozvala neka funckija ili event koji bi signalizirao da je server idle. 3 ce uvek biti aktivne(vidi se na osnovu maina) i to predstavlja idle za konkretan primer
            {
                logger.Log("Server is idle");
                logger.Write();
                cache.ClearCache(40);// moguce je koristiti neku funkciju koja vraca broj koji predstavlja procenat koliko osloboditi kes- memorije na osnovu stanja okruzenja. Ovde je uzeto najprostiji slucaj: prepoloviti kes svaki put kada se pozove metoda
            }
        }
    }
}
