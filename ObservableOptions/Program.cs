using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

ObservableCollection<KeyValuePair<string, string?>> inMemoryOptions = new()
{
    new("MyOptions:MyProperty", "Foo"),
};

var builder = Host.CreateDefaultBuilder();

builder
    .ConfigureLogging((context, logger) =>
    {
        logger.AddFilter("Microsoft.Hosting.Lifetime", _ => false);
    })
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<MySingletonService>();

        services.MonitorChangesFor<MyOptions>(inMemoryOptions);

        services
            .AddOptions<MyOptions>()
            .BindConfiguration(MyOptions.SectionKey);
    })
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddObservableCollection(inMemoryOptions);
    });

var host = builder.Build();

host.Start();

foreach (var value in new[] { "Bar", "Fu" })
{
    inMemoryOptions.Update("MyOptions:MyProperty", value);
    await Task.Delay(200);
}

await host.StopAsync();

host.WaitForShutdown();

public sealed class MyOptions
{
    public const string SectionKey = "MyOptions";

    public string MyProperty { get; set; } = string.Empty;
}

public sealed class MySingletonService : BackgroundService
{
    private IDisposable? _optionsChangedListener;
    private MyOptions _myCurrentOptions;
    private IServiceScopeFactory _serviceScopeFactory;

    public MySingletonService(IOptionsMonitor<MyOptions> optionsMonitor, IServiceScopeFactory serviceScopeFactory)
    {
        _optionsChangedListener = optionsMonitor.OnChange(MyOptionsChanged);
        _myCurrentOptions = optionsMonitor.CurrentValue;
        _serviceScopeFactory = serviceScopeFactory;
    }

    private void MyOptionsChanged(MyOptions newOptions)
    {
        _myCurrentOptions = newOptions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();

            scope
                .ServiceProvider
                .GetRequiredService<ILogger<MySingletonService>>()
                .LogInformation("{MyProperty}", _myCurrentOptions.MyProperty);

            await Task.Delay(100, stoppingToken);
        }
    }

    public override void Dispose()
    {
        _optionsChangedListener?.Dispose();

        base.Dispose();
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection MonitorChangesFor<TOptions>(
        this IServiceCollection services,
        ObservableCollection<KeyValuePair<string, string?>> inMemoryOptions)
        where TOptions : class
    => services.AddSingleton<IOptionsChangeTokenSource<TOptions>>(_ => new ObservableCollectionChangeTokenSource<TOptions>(inMemoryOptions));

    public static IServiceCollection MonitorChangesFor<TOptions>(
        this IServiceCollection services,
        Func<IServiceProvider, ObservableCollection<KeyValuePair<string, string?>>> implementationFactory)
        where TOptions : class
    => services.AddSingleton<IOptionsChangeTokenSource<TOptions>>(provider => new ObservableCollectionChangeTokenSource<TOptions>(implementationFactory(provider)));
}

public static class ListExtensions
{
    public static void Update(this IList<KeyValuePair<string, string?>> inMemoryOptions, string key, string value)
    {
        var old = inMemoryOptions.SingleOrDefault(x => x.Key.Equals(key));

        if (old.Key is null)
        {
            throw new NotSupportedException("Can only update existing options.");
        }

        var index = inMemoryOptions.IndexOf(old);

        inMemoryOptions[index] = (new(key, value));
    }
}

/// <summary>
/// IConfigurationBuilder extension methods for the ObservableCollectionConfigurationProvider.
/// </summary>
public static class ObservableCollectionConfigurationBuilderExtensions
{
    /// <summary>
        /// Adds the ObservableCollection configuration provider to <paramref name="configurationBuilder"/>.
        /// </summary>
        /// <param name="configurationBuilder">The <see cref="IConfigurationBuilder"/> to add to.</param>
        /// <param name="initialData">The data to add to ObservableCollection configuration provider.</param>
        /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
    public static IConfigurationBuilder AddObservableCollection(
        this IConfigurationBuilder configurationBuilder,
        ObservableCollection<KeyValuePair<string, string?>> initialData)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Add(new ObservableCollectionConfigurationSource { InitialData = initialData });

        return configurationBuilder;
    }
}

/// <summary>
/// Creates <see cref="IChangeToken"/>s so that <see cref="IOptionsMonitor{TOptions}"/> gets
/// notified when <see cref="IConfiguration"/> changes.
/// </summary>
/// <typeparam name="TOptions"></typeparam>
public sealed class ObservableCollectionChangeTokenSource<TOptions> : IOptionsChangeTokenSource<TOptions>
{
    private INotifyCollectionChanged _collection;

    /// <summary>
    /// Constructor taking the <see cref="IConfiguration"/> instance to watch.
    /// </summary>
    /// <param name="config">The configuration instance.</param>
    public ObservableCollectionChangeTokenSource(INotifyCollectionChanged collection)
        : this(Options.DefaultName, collection)
    { }

    /// <summary>
    /// Constructor taking the <see cref="IConfiguration"/> instance to watch.
    /// </summary>
    /// <param name="name">The name of the options instance being watched.</param>
    /// <param name="config">The configuration instance.</param>
    public ObservableCollectionChangeTokenSource(string? name, INotifyCollectionChanged collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        _collection = collection;
        Name = name ?? Options.DefaultName;
    }

    /// <summary>
    /// The name of the option instance being changed.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Returns the reloadToken from the <see cref="IConfiguration"/>.
    /// </summary>
    /// <returns></returns>
    public IChangeToken GetChangeToken()
    {
        return new ObservableCollectionChangeToken(_collection);
    }
}

/// <summary>
/// Represents an ObservableCollection as an <see cref="IConfigurationSource"/>.
/// </summary>
public sealed class ObservableCollectionConfigurationSource : IConfigurationSource
{
    /// <summary>
        /// The initial key value configuration pairs.
        /// </summary>
    public required ObservableCollection<KeyValuePair<string, string?>> InitialData { get; set; }

    /// <summary>
        /// Builds the <see cref="ObservableCollectionConfigurationProvider"/> for this source.
        /// </summary>
        /// <param name="builder">The <see cref="IConfigurationBuilder"/>.</param>
        /// <returns>A <see cref="ObservableCollectionConfigurationProvider"/></returns>
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new ObservableCollectionConfigurationProvider(this);
    }

    public IChangeToken Watch()
    {
        return new ObservableCollectionChangeToken(InitialData);
    }
}

/// <summary>
/// ObservableCollection implementation of <see cref="IConfigurationProvider"/>
/// </summary>
public sealed class ObservableCollectionConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly IDisposable? _changeTokenRegistration;
    private readonly ObservableCollectionConfigurationSource _source;

    /// <summary>
        /// Initialize a new instance from the source.
        /// </summary>
        /// <param name="source">The source settings.</param>
    public ObservableCollectionConfigurationProvider(ObservableCollectionConfigurationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;

        Load();

        _changeTokenRegistration = ChangeToken.OnChange(
        () => _source.Watch(),
        () =>
        {
            Load();
        });
    }

    public override void Load()
    {
        Data = new Dictionary<string, string?>();

        foreach (var (key, value) in _source.InitialData)
        {
            Data.Add(key, value);
        }
    }

    #region Dispose
    private bool _disposedValue;

    private void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _changeTokenRegistration?.Dispose();
            }

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    #endregion
}

public sealed class ObservableCollectionChangeToken : IChangeToken
{
    private CancellationTokenSource _cts = new();

    public ObservableCollectionChangeToken(INotifyCollectionChanged collection)
    {
        collection.CollectionChanged += OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        _cts.Cancel();
    }

    public bool ActiveChangeCallbacks { get; private set; } = true;

    /// <summary>
        /// True when the collection has changed since the change token was created. Once the collection changes, this value is always true
        /// </summary>
        /// <remarks>
        /// Once true, the value will always be true. Change tokens should not re-used once expired. The caller should discard this
        /// instance once it sees <see cref="HasChanged" /> is true.
        /// </remarks>
    public bool HasChanged => _cts.IsCancellationRequested;

    public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
    {
        return _cts.Token.Register(callback, state);
    }
}
