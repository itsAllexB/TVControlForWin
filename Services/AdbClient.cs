// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace TVControlForWin.Services;

// A minimal ADB (Android Debug Bridge) client over TCP: just enough to authenticate and run
// one shell command at a time. It exists so the app can reach settings that the Android TV
// Remote protocol has no channel for (picture brightness) without shipping adb.exe.
//
// Wire format: every message is a 24-byte little-endian header
//   command, arg0, arg1, payload length, payload checksum (byte sum), command ^ 0xFFFFFFFF
// followed by the payload. A session goes
//   → CNXN                      ← AUTH(token)
//   → AUTH(signature of token)  ← CNXN            (key already trusted)
//                           or  ← AUTH(token) again, then
//   → AUTH(public key)          ← CNXN            (after "Allow USB debugging?" is accepted)
// and each shell command is its own stream: OPEN "shell:<cmd>" → OKAY, WRTE…, CLSE.

/// <summary>The TV turned the connection down in a way retrying won't fix.</summary>
public sealed class AdbException : Exception
{
    public AdbException(string message, bool needsApproval = false) : base(message) =>
        NeedsApproval = needsApproval;

    /// <summary>True when the TV doesn't know our key and we were told not to ask for approval.</summary>
    public bool NeedsApproval { get; }
}

internal sealed class AdbClient : IDisposable
{
    public const int DefaultPort = 5555;

    private const uint CmdConnect = 0x4e584e43; // CNXN
    private const uint CmdAuth = 0x48545541;    // AUTH
    private const uint CmdOpen = 0x4e45504f;    // OPEN
    private const uint CmdOkay = 0x59414b4f;    // OKAY
    private const uint CmdClose = 0x45534c43;   // CLSE
    private const uint CmdWrite = 0x45545257;   // WRTE
    private const uint CmdStartTls = 0x534c5453; // STLS

    private const uint AuthToken = 1, AuthSignature = 2, AuthPublicKey = 3;

    // The original protocol version: small payloads, checksums on. Every adbd accepts it.
    private const uint ProtocolVersion = 0x01000000;
    private const uint MaxPayload = 4096;
    private const int MaxIncomingPayload = 1 << 20;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private uint _nextLocalId;

    public bool IsConnected => _tcp is { Connected: true } && _stream is not null;

    /// <summary>Creates the RSA key that identifies this app to a TV, as base64 PKCS#8.</summary>
    public static string CreateKey()
    {
        using var rsa = RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
    }

    /// <summary>
    /// Connects and authenticates with <paramref name="keyBase64"/> (from <see cref="CreateKey"/>).
    /// If the TV doesn't know the key yet it asks the person in front of it to allow debugging;
    /// <paramref name="onApprovalNeeded"/> fires at that point and the call then waits up to
    /// <paramref name="approvalTimeout"/> for the answer. Pass <see cref="TimeSpan.Zero"/> to
    /// never put that dialog on the TV: an unknown key then fails with
    /// <see cref="AdbException.NeedsApproval"/> instead.
    /// </summary>
    public async Task ConnectAsync(
        string host, int port, string keyBase64, string keyName,
        Action? onApprovalNeeded, TimeSpan approvalTimeout, CancellationToken ct)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(keyBase64), out _);

        var tcp = new TcpClient { NoDelay = true };
        _tcp = tcp;
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(4000);
            await tcp.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
        }
        _stream = tcp.GetStream();

        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(6000);

        await SendAsync(CmdConnect, ProtocolVersion, MaxPayload, Encoding.ASCII.GetBytes("host::\0"), handshake.Token)
            .ConfigureAwait(false);

        var signatureTried = false;
        while (true)
        {
            var (command, arg0, _, payload) = await ReceiveAsync(handshake.Token).ConfigureAwait(false);
            switch (command)
            {
                case CmdConnect:
                    return;

                case CmdAuth when arg0 == AuthToken && !signatureTried:
                    // The token stands in for a SHA-1 digest: sign it as one, PKCS#1 v1.5.
                    signatureTried = true;
                    var signature = rsa.SignHash(payload, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
                    await SendAsync(CmdAuth, AuthSignature, 0, signature, handshake.Token).ConfigureAwait(false);
                    break;

                case CmdAuth when arg0 == AuthToken:
                    // Signature not recognised: offer the public key, which puts the
                    // "Allow USB debugging?" dialog on the TV. Give a human time to answer.
                    if (approvalTimeout <= TimeSpan.Zero)
                        throw new AdbException("The TV hasn't approved this PC for debugging.", needsApproval: true);
                    var publicKey = Encoding.ASCII.GetBytes(EncodePublicKey(rsa.ExportParameters(false)) + " " + keyName + "\0");
                    await SendAsync(CmdAuth, AuthPublicKey, 0, publicKey, handshake.Token).ConfigureAwait(false);
                    onApprovalNeeded?.Invoke();
                    handshake.CancelAfter(approvalTimeout);
                    break;

                case CmdStartTls:
                    throw new AdbException("This TV only accepts encrypted ADB (wireless debugging), which isn't supported.");

                default:
                    throw new AdbException("Unexpected reply while connecting to ADB.");
            }
        }
    }

    /// <summary>Runs one shell command on the TV and returns everything it printed.</summary>
    public async Task<string> ShellAsync(string command, CancellationToken ct)
    {
        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stream is null) throw new IOException("Not connected.");

            var localId = ++_nextLocalId;
            await SendAsync(CmdOpen, localId, 0, Encoding.UTF8.GetBytes("shell:" + command + "\0"), ct)
                .ConfigureAwait(false);

            using var output = new MemoryStream();
            uint remoteId = 0;
            while (true)
            {
                var (cmd, arg0, arg1, payload) = await ReceiveAsync(ct).ConfigureAwait(false);
                if (arg1 != localId) continue; // a leftover from an earlier, abandoned stream

                switch (cmd)
                {
                    case CmdOkay:
                        remoteId = arg0;
                        break;
                    case CmdWrite:
                        remoteId = arg0;
                        output.Write(payload);
                        await SendAsync(CmdOkay, localId, remoteId, Array.Empty<byte>(), ct).ConfigureAwait(false);
                        break;
                    case CmdClose:
                        // arg0 == 0 here means the TV refused to open the stream at all.
                        if (arg0 != 0)
                            await SendAsync(CmdClose, localId, arg0, Array.Empty<byte>(), ct).ConfigureAwait(false);
                        else if (remoteId == 0)
                            throw new AdbException("The TV refused to run the command.");
                        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
                }
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task SendAsync(uint command, uint arg0, uint arg1, byte[] payload, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("Not connected.");
        var message = new byte[24 + payload.Length];
        var header = message.AsSpan(0, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], command);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], arg0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], arg1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Checksum(payload));
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], command ^ 0xFFFFFFFF);
        payload.CopyTo(message, 24);
        await stream.WriteAsync(message, ct).ConfigureAwait(false);
    }

    private async Task<(uint Command, uint Arg0, uint Arg1, byte[] Payload)> ReceiveAsync(CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("Not connected.");
        var header = new byte[24];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        var command = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if ((command ^ 0xFFFFFFFF) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)) || length > MaxIncomingPayload)
            throw new IOException("Malformed ADB message.");

        var payload = new byte[length];
        if (length > 0) await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return (command,
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)),
            payload);
    }

    private static uint Checksum(byte[] payload)
    {
        uint sum = 0;
        foreach (var b in payload) sum += b;
        return sum;
    }

    // ADB's own public-key format (Android's "mincrypt" RSAPublicKey struct, base64):
    //   uint32 length in 32-bit words (64), uint32 n0inv = -1/n mod 2^32,
    //   n[64] and rr[64] = (2^2048)^2 mod n as little-endian words, uint32 exponent.
    // The two derived values let the device verify with Montgomery multiplication.
    private static string EncodePublicKey(RSAParameters key)
    {
        const int Bytes = 256;
        var n = new BigInteger(key.Modulus, isUnsigned: true, isBigEndian: true);
        var word = BigInteger.One << 32;

        // Odd numbers form a group of order 2^31 modulo 2^32, so n^(2^31 - 1) is n's inverse.
        var inverse = BigInteger.ModPow(n % word, (BigInteger.One << 31) - 1, word);
        var n0inv = (uint)(word - inverse);
        var rr = BigInteger.ModPow(BigInteger.One << (Bytes * 8), 2, n);
        var exponent = new BigInteger(key.Exponent, isUnsigned: true, isBigEndian: true);

        var buffer = new byte[4 + 4 + Bytes + Bytes + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), Bytes / 4);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), n0inv);
        n.ToByteArray(isUnsigned: true, isBigEndian: false).CopyTo(buffer, 8);
        rr.ToByteArray(isUnsigned: true, isBigEndian: false).CopyTo(buffer, 8 + Bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8 + 2 * Bytes), (uint)exponent);
        return Convert.ToBase64String(buffer);
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }
}
