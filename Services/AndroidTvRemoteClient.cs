// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TVControlForWin.Services;

// Android TV Remote protocol v2 — what the Google TV phone remote speaks, and what every
// Android TV / Google TV set (TCL, Sony, Philips, Hisense, Xiaomi, Chromecast, Shield…)
// answers on the LAN. Two TLS ports, both authenticated by a self-signed client certificate:
//
//   6467  pairing: the TV shows a 6-character code; we prove we saw it by sending a hash of
//         both certificates' public keys plus the code. After that the TV trusts our cert.
//   6466  remote:  key presses out, power / volume state back, with a ping every few seconds.
//
// Messages are protobuf, each preceded by its varint length (see ProtoWire.cs).
// Prior art: tronikos/androidtvremote2 (what Home Assistant uses).

/// <summary>The pairing code the user typed can't be the one the TV is showing.</summary>
public sealed class PairingCodeException : Exception
{
    public PairingCodeException(string message) : base(message) { }
}

internal static class AndroidTvTls
{
    public const int RemotePort = 6466;
    public const int PairingPort = 6467;

    /// <summary>
    /// Creates the self-signed certificate that identifies this app to a TV, plus its
    /// PKCS#12 form (base64, private key included) for persisting alongside the device.
    /// </summary>
    public static (X509Certificate2 Certificate, string Base64Pfx) CreateClientCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=TVControlForWin", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(25));
        var pfx = Convert.ToBase64String(ephemeral.Export(X509ContentType.Pfx));
        return (LoadClientCertificate(pfx), pfx);
    }

    /// <summary>
    /// Loads a certificate saved by <see cref="CreateClientCertificate"/>. It always goes
    /// through PKCS#12 because Windows' TLS stack can't use a key that only exists in memory.
    /// </summary>
    public static X509Certificate2 LoadClientCertificate(string base64Pfx) =>
        new(Convert.FromBase64String(base64Pfx), (string?)null, X509KeyStorageFlags.DefaultKeySet);

    /// <summary>Opens a TLS connection presenting <paramref name="clientCertificate"/>. The TV's own
    /// certificate is self-signed, so it's accepted as-is — trust comes from the pairing code.</summary>
    public static async Task<SslStream> ConnectAsync(
        string host, int port, X509Certificate2 clientCertificate, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ClientCertificates = new X509CertificateCollection { clientCertificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, ct).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }
}

/// <summary>
/// One pairing attempt on port 6467. <see cref="StartAsync"/> makes the TV display its code;
/// <see cref="FinishAsync"/> submits what the user typed. Dispose to dismiss the code.
/// </summary>
public sealed class AndroidTvPairingSession : IDisposable
{
    private const int StatusOk = 200;
    private const int HexEncoding = 3;
    private const int CodeLength = 6;
    private const int InputRole = 1;

    private readonly SslStream _ssl;
    private readonly X509Certificate2 _clientCertificate;

    private AndroidTvPairingSession(SslStream ssl, X509Certificate2 clientCertificate)
    {
        _ssl = ssl;
        _clientCertificate = clientCertificate;
    }

    /// <summary>
    /// Connects and negotiates up to the point where the TV is showing its pairing code.
    /// <paramref name="clientName"/> is the name the TV displays for this app.
    /// </summary>
    public static async Task<AndroidTvPairingSession> StartAsync(
        string host, X509Certificate2 clientCertificate, string clientName, CancellationToken ct)
    {
        var ssl = await AndroidTvTls.ConnectAsync(host, AndroidTvTls.PairingPort, clientCertificate, ct)
            .ConfigureAwait(false);
        var session = new AndroidTvPairingSession(ssl, clientCertificate);
        try
        {
            ProtoWriter Encoding() => new ProtoWriter().Varint(1, HexEncoding).Varint(2, CodeLength);

            // pairing_request → pairing_request_ack
            await session.ExchangeAsync(10,
                new ProtoWriter().String(1, "atvremote").String(2, clientName), ct).ConfigureAwait(false);
            // pairing_option: we take a 6-digit hex code as input → the TV echoes its options
            await session.ExchangeAsync(20,
                new ProtoWriter().Message(1, Encoding()).Varint(3, InputRole), ct).ConfigureAwait(false);
            // pairing_configuration → ack, and the code appears on screen
            await session.ExchangeAsync(30,
                new ProtoWriter().Message(1, Encoding()).Varint(2, InputRole), ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Submits the code shown on the TV. Throws <see cref="PairingCodeException"/> — leaving the
    /// session usable for another try — when the code is mistyped, which is detectable locally:
    /// its first byte must equal the first byte of the secret hash.
    /// </summary>
    public async Task FinishAsync(string code, CancellationToken ct)
    {
        code = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if (code.Length != CodeLength || !code.All(Uri.IsHexDigit))
            throw new PairingCodeException("Codes have 6 characters, 0 to 9 and A to F");

        var codeBytes = Convert.FromHexString(code);
        var secret = ComputeSecret(codeBytes);
        if (secret[0] != codeBytes[0])
            throw new PairingCodeException("Wrong code. Try again");

        // pairing_secret → pairing_secret_ack
        await ExchangeAsync(40, new ProtoWriter().Bytes(1, secret), ct).ConfigureAwait(false);
    }

    // SHA-256 over both RSA public keys (client then server, modulus then exponent, as
    // minimal big-endian integers) followed by the last two bytes of the code.
    private byte[] ComputeSecret(byte[] codeBytes)
    {
        using var serverCertificate = new X509Certificate2(_ssl.RemoteCertificate
            ?? throw new IOException("The TV presented no certificate."));
        using var clientRsa = _clientCertificate.GetRSAPublicKey();
        using var serverRsa = serverCertificate.GetRSAPublicKey();
        if (clientRsa is null || serverRsa is null)
            throw new IOException("Pairing needs RSA certificates on both sides.");
        var client = clientRsa.ExportParameters(false);
        var server = serverRsa.ExportParameters(false);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Minimal(client.Modulus!));
        hash.AppendData(Minimal(client.Exponent!));
        hash.AppendData(Minimal(server.Modulus!));
        hash.AppendData(Minimal(server.Exponent!));
        hash.AppendData(codeBytes.AsSpan(1));
        return hash.GetHashAndReset();

        static ReadOnlySpan<byte> Minimal(byte[] bigEndian) => bigEndian.AsSpan().TrimStart((byte)0);
    }

    // Sends one pairing step (wrapped in the protocol-version + status envelope) and reads the
    // TV's answer, which must carry status 200.
    private async Task ExchangeAsync(int field, ProtoWriter body, CancellationToken ct)
    {
        var message = new ProtoWriter().Varint(1, 2).Varint(2, StatusOk).Message(field, body).ToArray();
        await ProtoFraming.WriteAsync(_ssl, message, ct).ConfigureAwait(false);

        var reply = ProtoMessage.Parse(await ProtoFraming.ReadAsync(_ssl, ct).ConfigureAwait(false));
        var status = reply.Varint(2) ?? 0;
        if (status != StatusOk)
            throw new IOException($"The TV refused the pairing step (status {status}).");
    }

    public void Dispose() => _ssl.Dispose();
}

/// <summary>
/// The remote-control connection on port 6466: sends key presses and tracks whether the TV is
/// on. One instance == one connection; create a new one to reconnect.
/// </summary>
public sealed class AndroidTvRemoteClient : IDisposable
{
    /// <summary>KEYCODE_POWER — a toggle, so only send it when <see cref="IsOn"/> says which way it'll go.</summary>
    public const int KeyPower = 26;

    // Feature bits we claim in the handshake: key | ime | voice | power | volume | app-link.
    // The same value the reference clients send; the TV only reports power state when asked.
    private const int Features = 622;

    // The TV pings every ~5 s. Going this long without hearing anything means the connection
    // is dead even if TCP hasn't noticed — e.g. the TV lost power.
    private const int IdleTimeoutMs = 20_000;

    private const int HandshakeTimeoutMs = 6_000;
    private const int ShortPress = 3;

    private SslStream? _ssl;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile bool _connected;
    private bool? _isOn;

    public bool IsConnected => _connected;

    /// <summary>Whether the TV is on (true), in standby (false), or hasn't said yet (null).</summary>
    public bool? IsOn => _isOn;

    /// <summary>Model and vendor as reported by the TV in the handshake, e.g. "Smart TV Pro" / "TCL".</summary>
    public string Model { get; private set; } = "";
    public string Vendor { get; private set; } = "";

    /// <summary>Raised from the receive loop whenever the TV reports its power state.</summary>
    public event Action<bool>? PowerChanged;

    /// <summary>
    /// Connects and completes the handshake. Success means the TV accepted our certificate; a TV
    /// we aren't paired with drops the connection instead, surfacing here as an exception.
    /// </summary>
    public async Task ConnectAsync(string host, X509Certificate2 clientCertificate, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeoutMs);
        _ssl = await AndroidTvTls.ConnectAsync(host, AndroidTvTls.RemotePort, clientCertificate, timeout.Token)
            .ConfigureAwait(false);

        // The TV speaks first (remote_configure). Wait for that one message before declaring
        // the connection good — it's the proof the certificate was accepted.
        var first = ProtoMessage.Parse(await ProtoFraming.ReadAsync(_ssl, timeout.Token).ConfigureAwait(false));
        _connected = true;
        await HandleAsync(first, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Pumps incoming messages until the connection ends, which it reports by throwing
    /// (<see cref="IOException"/> on a drop or silence, <see cref="OperationCanceledException"/>
    /// when <paramref name="ct"/> fires).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var ssl = _ssl ?? throw new InvalidOperationException("Not connected.");
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(IdleTimeoutMs);
                byte[] frame;
                try
                {
                    frame = await ProtoFraming.ReadAsync(ssl, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException("The TV stopped responding.");
                }
                await HandleAsync(ProtoMessage.Parse(frame), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _connected = false;
        }
    }

    private async Task HandleAsync(ProtoMessage message, CancellationToken ct)
    {
        if (message.Message(1) is { } configure)
        {
            // remote_configure: the TV introduces itself; answer with who we are.
            var info = configure.Message(2);
            Model = info?.String(1) ?? "";
            Vendor = info?.String(2) ?? "";
            await SendAsync(new ProtoWriter().Message(1, new ProtoWriter()
                .Varint(1, Features)
                .Message(2, new ProtoWriter()
                    .Varint(3, 1)
                    .String(4, "1")
                    .String(5, "atvremote")
                    .String(6, "1.0.0"))), ct).ConfigureAwait(false);
        }
        else if (message.Has(2))
        {
            // remote_set_active: confirm the feature set.
            await SendAsync(new ProtoWriter().Message(2, new ProtoWriter().Varint(1, Features)), ct)
                .ConfigureAwait(false);
        }
        else if (message.Message(8) is { } ping)
        {
            // remote_ping_request → remote_ping_response echoing its counter.
            await SendAsync(new ProtoWriter().Message(9, new ProtoWriter().Varint(1, ping.Varint(1) ?? 0)), ct)
                .ConfigureAwait(false);
        }
        else if (message.Message(40) is { } start)
        {
            // remote_start { started }: proto3 omits false, so an empty message means standby.
            var on = (start.Varint(1) ?? 0) != 0;
            _isOn = on;
            PowerChanged?.Invoke(on);
        }
        // Everything else (volume, current app, IME state) is ignored.
    }

    /// <summary>Sends a single key press (an Android KEYCODE_* value).</summary>
    public Task SendKeyAsync(int keyCode, CancellationToken ct) =>
        SendAsync(new ProtoWriter().Message(10,
            new ProtoWriter().Varint(1, (ulong)keyCode).Varint(2, ShortPress)), ct);

    /// <summary>Waits until the TV reports the given power state. False if it didn't in time.</summary>
    public async Task<bool> WaitForPowerAsync(bool on, int timeoutMs, CancellationToken ct)
    {
        for (var waited = 0; waited < timeoutMs; waited += 100)
        {
            if (_isOn == on) return true;
            if (!_connected) return false;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return _isOn == on;
    }

    /// <summary>Waits for the first power report after connecting (it trails the handshake by a
    /// moment). Returns <see cref="IsOn"/>, still null if the TV never said.</summary>
    public async Task<bool?> WaitForPowerReportAsync(int timeoutMs, CancellationToken ct)
    {
        for (var waited = 0; waited < timeoutMs && _isOn is null && _connected; waited += 100)
            await Task.Delay(100, ct).ConfigureAwait(false);
        return _isOn;
    }

    private async Task SendAsync(ProtoWriter message, CancellationToken ct)
    {
        var ssl = _ssl;
        if (ssl is null || !_connected) throw new IOException("Not connected.");
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ProtoFraming.WriteAsync(ssl, message.ToArray(), ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    public void Dispose()
    {
        _connected = false;
        try { _ssl?.Dispose(); } catch { }
        _ssl = null;
    }
}
