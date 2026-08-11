# Шпаргалка по DI (внедрение зависимостей)

Как устроено создание объектов в проекте и правила, которым следуем.
Контейнер — `Microsoft.Extensions.Hosting` (внутри него стандартный
`Microsoft.Extensions.DependencyInjection`).

---

## Главное правило

**Сервисы и ViewModel не создаются через `new` внутри кода.** Они
регистрируются в контейнере при старте и приходят через параметры конструктора.

```csharp
// Правильно: зависимость — параметр конструктора
public sealed class TagsViewModel
{
    private readonly IRuntimeClient _client;

    public TagsViewModel(IRuntimeClient client) => _client = client;
}

// Неправильно: класс сам создаёт свою зависимость
public sealed class TagsViewModel
{
    private readonly GrpcClient _client = new(); // запрещено
}
```

Конструктор — это объявление потребностей класса: «мне нужно X». Откуда
X берётся — решает контейнер, а не класс.

---

## Где регистрируются сервисы

| Проект | Точка регистрации |
|---|---|
| `SCADA` (клиент) | `Program.cs`, метод `Main`, `builder.Services.Add...` |
| `SCADA.Server` | `Program.cs`, `builder.Services.Add...` (контейнер встроен в ASP.NET) |

### Клиент

Хост собирается **до** запуска Avalonia, провайдер доступен как
`Program.Services`:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<MainViewModel>();
builder.Services.AddSingleton<MainWindow>();
Services = builder.Build().Services;
```

Окно и ViewModel страниц берутся из контейнера:
`Program.Services.GetRequiredService<MainWindow>()`.

### Сервер

```csharp
builder.Services.AddGrpc();
// наши сервисы:
builder.Services.AddSingleton<ITagTable>(new TagTable(20_000));
builder.Services.AddHostedService<PollingWorker>(); // фоновая задача
```

---

## Время жизни (что выбирать)

| Регистрация | Смысл | Когда использовать у нас |
|---|---|---|
| `AddSingleton<T>()` | Один экземпляр на всё приложение | `TagTable`, gRPC-клиент к серверу, конфигурация, кэши |
| `AddTransient<T>()` | Новый экземпляр при каждом запросе | ViewModel страниц, команды |
| `AddScoped<T>()` | Один экземпляр на область (на сервере — на запрос) | В gRPC-сервисах, если нужно состояние в рамках вызова |

Сомневаетесь между Singleton и Transient — смотрите, хранит ли объект
состояние, которое должно быть общим. `TagTable` общая → singleton.
ViewModel страницы своя у каждого открытого экрана → transient.

---

## Регистрация через интерфейс

Зависимости объявляем интерфейсами, регистрируем реализацию:

```csharp
builder.Services.AddSingleton<IRuntimeClient, GrpcClient>();
```

Класс знает только про `IRuntimeClient`. В тесте регистрируется заглушка:

```csharp
var vm = new TagsViewModel(new FakeRuntimeClient()); // без запуска приложения
```

Это же даёт шов «клиент не знает про PLC»: UI зависит от контракта,
а не от конкретного транспорта.

---

## Фоновые задачи (только сервер)

Фоновая работа оформляется наследником `BackgroundService`:

```csharp
public sealed class PollingWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // цикл опроса
        }
    }
}
```

Хост сам запускает задачу при старте и при завершении приложения отменяет
`stoppingToken` — свой механизм остановки писать не нужно.
`CancellationToken` обязателен во всех асинхронных вызовах внутри.

---

## Ловушки и особые случаи

### Конструктор без параметров у окон/контролов Avalonia

XAML-загрузчику и дизайнеру IDE нужен **публичный конструктор без параметров**.
Если его нет — предупреждение `AVLN3001`, превью в дизайнере не работает.
Паттерн для окон:

```csharp
// Для дизайнера Avalonia и XAML-загрузчика
public MainWindow() : this(new MainViewModel()) { }

// Для DI-контейнера
public MainWindow(MainViewModel viewModel)
{
    InitializeComponent();
    DataContext = viewModel;
}
```

### `Program.Services` — только для корня

Статический `Program.Services` используем ровно в одном месте — чтобы достать
корневое окно. Протаскивать его внутрь классов и дёргать `GetService` из
бизнес-логики — антипаттерн (service locator): зависимости перестают быть
видимыми, тесты ломаются. Внутри классов — только конструктор.

### Не регистрируйте то, что не является сервисом

Модели данных (`TagValue`, `TagDefinition`), DTO, простые структуры — это не
сервисы, они создаются как обычно. DI — для долгоживущих объектов с
поведением: клиенты, движки, кэши, ViewModel, окна.

---

## Новый пакет / новый сервис — чек-лист

1. Пакет: `dotnet add <проект> package <имя>` — версия сама ляжет в
   `Directory.Packages.props` (CPM).
2. Сервис: интерфейс в `SCADA.Core` (или рядом с реализацией, если локальный),
   реализация в своём проекте.
3. Регистрация в `Program.cs` соответствующего приложения с правильным
   временем жизни.
4. Потребители получают зависимость через конструктор.
5. Сборка: `dotnet build SCADA.sln` — предупреждения = ошибки
   (`TreatWarningsAsErrors` включён в `Directory.Build.props`).
