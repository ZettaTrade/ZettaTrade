namespace ZettaTrade.App.ViewModels;

using System.Globalization;
using ReactiveUI;
using ZettaTrade.AI.Mcp;
using ZettaTrade.Core.Interfaces;

/// <summary>
/// One external MCP server's draft row in the settings' Mcp tab: the fields
/// the user edits (name, transport, the per-transport endpoint pieces, the
/// enabled flag, the auth secret reference) plus the honest connection state
/// the client manager's snapshots publish. A pure draft — only the save maps
/// it into the persisted <see cref="McpServerConfig"/>; no field combination
/// is silently coerced: unusable values ride the configs verbatim and the
/// transport boundary fails them loudly into the entry's Failed state.
/// </summary>
public sealed class McpServerRow : ReactiveObject
{
    /// <summary>The scheme the HTTP row writes its URL with; seeded from a config (kept on round-trips), http for fresh rows.</summary>
    private string _scheme = "http";

    private string _name = string.Empty;

    /// <summary>The transport pick backer: 0 = Stdio, 1 = StreamableHttp (the <see cref="McpTransport"/> order).</summary>
    private int _transportIndex;

    private string _address = string.Empty;

    private string _portText = string.Empty;

    private string _command = string.Empty;

    private string _argsText = string.Empty;

    private string _authSecretRef = string.Empty;

    private string _timeoutText = string.Empty;

    private bool _isEnabled = true;

    private bool _isStateKnown;

    private bool _isDisabled;

    private bool _isConnecting;

    private bool _isConnected;

    private bool _isFailed;

    private string _detailText = string.Empty;

    #region Draft Fields

    /// <summary>
    /// The entry's name — the key the manager's connections and states key
    /// by; the save drops a blank-named row with a logged warning (a
    /// nameless entry keys nothing).
    /// </summary>
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    /// <summary>
    /// The transport pick: 0 = Stdio (command + args), 1 = StreamableHttp
    /// (address + port). The conditional per-transport fields ride the
    /// flags it derives.
    /// </summary>
    /// <summary>
    /// The transport pick: 0 = Stdio (command + args), 1 = StreamableHttp
    /// (address + port). The conditional per-transport fields ride the
    /// flags it derives.
    /// </summary>
    public int TransportIndex
    {
        get => _transportIndex;
        set
        {
            if (_transportIndex == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _transportIndex, value);
            this.RaisePropertyChanged(nameof(IsStdio));
            this.RaisePropertyChanged(nameof(IsHttp));
        }
    }

    /// <summary>Whether the row edits a stdio entry (the command + args fields show).</summary>
    public bool IsStdio => TransportIndex == (int)McpTransport.Stdio;

    /// <summary>Whether the row edits an HTTP entry (the address + port + timeout fields show).</summary>
    public bool IsHttp => TransportIndex == (int)McpTransport.StreamableHttp;

    /// <summary>HTTP host or address (ip or name); stdio rows ignore it.</summary>
    public string Address
    {
        get => _address;
        set => this.RaiseAndSetIfChanged(ref _address, value);
    }

    /// <summary>
    /// HTTP port as typed (free text, empty = the scheme's default port —
    /// URL semantics, nothing substituted); the transport boundary rejects
    /// an unparsable combination loudly.
    /// </summary>
    public string PortText
    {
        get => _portText;
        set => this.RaiseAndSetIfChanged(ref _portText, value);
    }

    /// <summary>Stdio executable path or name (resolved over the shell's PATH by the OS process API).</summary>
    public string Command
    {
        get => _command;
        set => this.RaiseAndSetIfChanged(ref _command, value);
    }

    /// <summary>Stdio arguments as space-separated text (an argument containing spaces does not fit — the hint documents it).</summary>
    public string ArgsText
    {
        get => _argsText;
        set => this.RaiseAndSetIfChanged(ref _argsText, value);
    }

    /// <summary>
    /// The auth secret's reference key only — it resolves through the
    /// <see cref="ISecretStore"/>; the secret itself never rides config.
    /// </summary>
    public string AuthSecretRef
    {
        get => _authSecretRef;
        set => this.RaiseAndSetIfChanged(ref _authSecretRef, value);
    }

    /// <summary>The HTTP connect timeout in seconds (empty = the transport's shipped default); free text as typed.</summary>
    public string TimeoutText
    {
        get => _timeoutText;
        set => this.RaiseAndSetIfChanged(ref _timeoutText, value);
    }

    /// <summary>Whether the entry dials at all (unchecked entries persist with Disabled state and never connect).</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
    }

    #endregion

    #region State Surface

    /// <summary>Whether the manager's live state covers the row (a wiped snapshot leaves the honest "not known" dash).</summary>
    public bool IsStateKnown
    {
        get => _isStateKnown;
        private set => this.RaiseAndSetIfChanged(ref _isStateKnown, value);
    }

    /// <summary>The saved-but-disabled row: it never dials.</summary>
    public bool IsDisabled
    {
        get => _isDisabled;
        private set => this.RaiseAndSetIfChanged(ref _isDisabled, value);
    }

    /// <summary>The handshake is in flight.</summary>
    public bool IsConnecting
    {
        get => _isConnecting;
        private set => this.RaiseAndSetIfChanged(ref _isConnecting, value);
    }

    /// <summary>The transport came up.</summary>
    public bool IsConnected
    {
        get => _isConnected;
        private set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    /// <summary>The connect could not complete; the reason rides <see cref="DetailText"/>.</summary>
    public bool IsFailed
    {
        get => _isFailed;
        private set => this.RaiseAndSetIfChanged(ref _isFailed, value);
    }

    /// <summary>The failure detail of the Failed state (empty otherwise); the manager logs the same text.</summary>
    public string DetailText
    {
        get => _detailText;
        private set => this.RaiseAndSetIfChanged(ref _detailText, value);
    }

    /// <summary>
    /// Apply the row's lifecycle state from the manager's snapshot (or rays
    /// all to the naive "not known" row when the entry has no state yet).
    /// Kinds are mutually exclusive by construction; bools+detail ride one
    /// method so every row repaint is atomic.
    /// </summary>
    public void SetState(McpClientState? state)
    {
        if (state is not { } live)
        {
            IsStateKnown = false;
            IsDisabled = false;
            IsConnecting = false;
            IsConnected = false;
            IsFailed = false;
            DetailText = string.Empty;
            return;
        }

        IsStateKnown = true;
        IsDisabled = live.State == McpClientStateKind.Disabled;
        IsConnecting = live.State == McpClientStateKind.Connecting;
        IsConnected = live.State == McpClientStateKind.Connected;
        IsFailed = live.State == McpClientStateKind.Failed;
        DetailText = live.State == McpClientStateKind.Failed && live.Detail is { } detail ? detail : string.Empty;
    }

    #endregion

    #region Config Conversion

    /// <summary>
    /// Seed the draft fields from one persisted config (the settings tab's
    /// round-trip). A URL the row's address+port shape cannot express (one
    /// with a path) seeds its host and port and loses the path — the row
    /// edits exactly the shape the task defines.
    /// </summary>
    public static McpServerRow FromConfig(McpServerConfig config)
    {
        var row = new McpServerRow
        {
            Name = config.Name,
            TransportIndex = (int)config.Transport,
            AuthSecretRef = config.AuthSecretRef ?? string.Empty,
            IsEnabled = config.Enabled,
            ArgsText = string.Join(" ", config.Args ?? []),
            Command = config.Command ?? string.Empty,
        };

        if (Uri.TryCreate(config.Url ?? string.Empty, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme is "http" or "https")
        {
            row._scheme = endpoint.Scheme;
            row.Address = endpoint.Host;
            row.PortText = endpoint.IsDefaultPort
                ? string.Empty
                : endpoint.Port.ToString(CultureInfo.InvariantCulture);
        }

        row._timeoutText = config.HttpTimeoutSeconds is { } timeout
            ? timeout.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

        return row;
    }

    /// <summary>
    /// Map the draft into the persisted config shape, verbatim: the port
    /// reads as typed (empty = the scheme's default, garbage rides as
    /// garbage), the timeout parses strict (an unparsable one stays out with
    /// the row's own report — nothing substituted) and the transport picks
    /// the config's URL from the address + port pair.
    /// </summary>
    /// <param name="problems">The conversion problems of this row (reported by the caller; empty when the row converts fully).</param>
    /// <returns>The config, or null when the row lacks a name (a nameless entry keys nothing — the save skips it).</returns>
    public McpServerConfig? ToConfig(List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return null;
        }

        List<string> rowProblems = [];
        string[]? args = null;
        string? command = null;
        string? url = null;
        var isHttp = (McpTransport)TransportIndex == McpTransport.StreamableHttp;

        if (isHttp)
        {
            url = BuildUrl(rowProblems);
        }
        else
        {
            command = Command.Trim().Length > 0 ? Command.Trim() : null;

            if (ArgsText.Trim().Length > 0)
            {
                args = [.. ArgsText.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
            }
        }

        int? timeout = null;

        if (TimeoutText.Trim().Length > 0)
        {
            if (int.TryParse(TimeoutText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                timeout = parsed;
            }
            else
            {
                rowProblems.Add($"the HTTP timeout '{TimeoutText.Trim()}' is not a readable integer; the entry saves without it");
            }
        }

        problems.AddRange(rowProblems.Select(problem => $"{Name}: {problem}"));

        return new McpServerConfig
        {
            Name = Name.Trim(),
            Transport = (McpTransport)TransportIndex,
            Command = command,
            Args = args,
            Url = url,
            AuthSecretRef = AuthSecretRef.Trim().Length > 0 ? AuthSecretRef.Trim() : null,
            Enabled = IsEnabled,
            HttpTimeoutSeconds = timeout,
        };
    }

    /// <summary>The HTTP entry's URL from the row's scheme + address + port (the transport boundary validates it loudly).</summary>
    private string BuildUrl(List<string> problems)
    {
        var trimmedAddress = Address.Trim();

        if (trimmedAddress.Length == 0)
        {
            problems.Add("the HTTP address is empty; the entry saves a URL the transport rejects");
        }

        var trimmedPort = PortText.Trim();

        return trimmedPort.Length == 0
            ? $"{_scheme}://{trimmedAddress}"
            : $"{_scheme}://{trimmedAddress}:{trimmedPort}";
    }

    #endregion
}
