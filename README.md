# AsyncServer — Sistemsko Programiranje, Projekat 2

Serverska aplikacija implementirana kao konzolni program u C#, zasnovana na taskovima i asinhronim operacijama. Obrađuje HTTP zahteve klijenata i generiše odgovore na osnovu podataka sa Rijksmuseum API-ja.

---

## Arhitektura sistema

Sistem je zasnovan na razdvajanju prijema i obrade zahteva:

- **Prijem** — glavna nit prima HTTP zahteve putem `HttpListener` i smešta ih u deljenu strukturu (`Queue<HttpListenerContext>`)
- **Obrada** — svaki zahtev se obrađuje u zasebnom tasku (`Task.Run`)
- **Sinhronizacija između prijema i obrade** — `SemaphoreSlim` (inicijalizovan na 0) koristi se umesto `Monitor.Pulse/Wait` kao async-kompatibilna alternativa

### Niti vs Taskovi

| Komponenta | Tip | Razlog |
|---|---|---|
| Prijem zahteva (Main) | Klasična nit (main) | Blokira na `GetContext()` — čekanje na klijenta |
| Obrada zahteva | `Task.Run` | Async I/O operacije ka API-ju |
| ShutdownThread | Klasična nit | Blokira na `Console.ReadLine()` — taskovi nemaju smisla |
| ClearingThread | Klasična nit | Periodični `Thread.Sleep` — taskovi nemaju smisla |

---

## Komponente

### Program.cs — Ulazna tačka

- `Main` prima zahteve i stavlja ih u queue, pa otpušta semafor i pokreće task za obradu
- `ProcessingThreadAsync` — async task koji čeka na semafor, uzima zahtev iz queue-a i obrađuje ga
- `ShutdownThread` — klasična nit, čeka unos "67" za gašenje servera; poziva `listener.Stop()` umesto `Environment.Exit`
- `ClearingThread` — klasična nit, svakih 10 sekundi poziva `ClearServer`

#### Kontinuacije (ContinueWith)

Korišćene na dva mesta gde je logovanje rezultata nakon završene async operacije prirodan use case:

```csharp
// Nakon dohvatanja liste ID-eva
List<long> ids = await server.RequestAsync(param)
    .ContinueWith(t => {
        logger.Log($"RequestAsync completed, returned {t.Result.Count} ids");
        return t.Result;
    });

// Nakon paralelnog dohvatanja svih URL-eva
string[] results = await Task.WhenAll(tasks)
    .ContinueWith(t => {
        logger.Log($"WhenAll completed, fetched {t.Result.Length} urls");
        return t.Result;
    });
```

### ProxyServer.cs — Proxy ka Rijksmuseum API-ju

- `RequestAsync` — async dohvatanje liste ID-eva sa API-ja
- `GetPicURLAsync` — async dohvatanje URL-a slike za dati ID, sa zaštitom od cache stampede
- `ClearServer` — proverava da li je server idle i oslobađa deo keša

#### Paralelno dohvatanje URL-eva

```csharp
List<Task<string>> tasks = new List<Task<string>>();
foreach (long id in ids)
{
    Task<string> t = server.GetPicURLAsync(id);
    tasks.Add(t);
}
string[] results = await Task.WhenAll(tasks);
```

Svi taskovi kreću paralelno, `Task.WhenAll` čeka da svi završe.

### Cache.cs — LRU keš

- Strategija upravljanja: **LRU (Least Recently Used)** sa ograničenjem veličine
- Thread-safe putem `lock` objekta
- `Add` — dodaje element, eviktuje LRU ako je keš pun
- `Get` — dohvata element i osvežava poziciju (Touch)
- `ClearCache(int percentage)` — oslobađa tačno određeni procenat keša

### Logger.cs — Thread-safe logovanje

- Baferovano logovanje putem `ConcurrentQueue<string>`
- `StreamWriter` nije thread-safe pa sve operacije nad njim idu u `lock`
- Automatski flush kada buffer dostigne limit (default: 10 poruka)
- Podrška za promenu izlaza (konzola / fajl) u toku rada

---

## Cache Stampede prevencija

Problem: više taskova istovremeno traži isti resurs koji nije u kešu — bez zaštite, svi bi krenuli da ga dohvataju.

Rešenje: `ConcurrentDictionary<long, SemaphoreSlim>` — per-ID semafor:

```
1. cache.Get(id)              ← prvi check (bez semafora, brzo)
2. sem = sems.GetOrAdd(id)    ← dohvati ili napravi semafor za taj ID
3. await sem.WaitAsync()      ← zaključaj
4. cache.Get(id)              ← drugi check (double-checked locking)
5. HTTP poziv ako nije u kešu
6. cache.Add(id, picURL)
7. sem.Release() + TryRemove  ← otpusti i ukloni semafor
```

Samo prvi task prolazi kroz HTTP poziv, ostali čekaju na semaforu i na drugom checku nađu rezultat u kešu.

---

## Kritične sekcije

| Sekcija | Mehanizam | Razlog |
|---|---|---|
| Queue enqueue/dequeue | `lock(lockObj)` | Queue nije thread-safe |
| Cache Add/Get/ClearCache | `lock(lockObject)` | LinkedList za LRU nije thread-safe |
| Logger Write | `lock(locker)` | StreamWriter nije thread-safe |
| Per-ID HTTP poziv | `SemaphoreSlim(1,1)` | Cache stampede prevencija, async-kompatibilno |

---

## Pokretanje

```bash
dotnet run
```

Server sluša na `http://localhost:5000/`.

Slanje zahteva:
```
http://localhost:5000/?page=1&pageSize=100&...
```

Gašenje servera: ukucati `67` u konzoli.

---

## Zavisnosti

- .NET 8+
- Rijksmuseum API (bez API ključa za linked data endpoint)
