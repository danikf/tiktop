using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using tik4net;
using tik4net.Objects;
using tik4net.Objects.Ip;
using tik4net.Objects.Tool;

namespace tiktop
{
    public class MikrotikWrapper: IDisposable
    {
        ITikConnection _connection;
        ITikCommand? _torchCmd;

        public MikrotikWrapper(string host, string user, string pass, bool useSsl = true, int port = 8729)
        {
            var connType = useSsl ? TikConnectionType.ApiSsl : TikConnectionType.Api;
            // tik4net 4.0: AllowInvalidCertificate defaults to false and now also applies to API-SSL.
            // RouterOS presents a self-signed certificate by default, so it must be opted in explicitly.
            var setup = new TikConnectionSetup(host, user, pass)
            {
                Port = port,
                AllowInvalidCertificate = true,
            };
            _connection = setup.Create(connType);
        }

        public void StartListening(string iface, Action<ToolTorch> onItemCallback, Action<Exception>? onError = null)
        {
            _torchCmd = _connection.LoadWithCallback<ToolTorch>(
                onItemCallback,
                ex => onError?.Invoke(ex),
                _connection.CreateParameter("interface", iface),
                _connection.CreateParameter("port", "any"),
                _connection.CreateParameter("src-address", "0.0.0.0/0"),
                _connection.CreateParameter("dst-address", "0.0.0.0/0"));
        }

        public IReadOnlyList<IPNetwork> GetLocalNetworks(string iface)
        {
            var addresses = _connection.LoadList<IpAddress>(
                _connection.CreateParameter("interface", iface));

            return addresses
                .Where(a => !(a.Disabled ?? false) && !a.Invalid)
                .Select(a => ParseNetwork(a.Address))
                .OfType<IPNetwork>()
                .ToList();
        }

        private static IPNetwork? ParseNetwork(string? cidr)
        {
            try
            {
                if (cidr == null) return null;
                var slash = cidr.IndexOf('/');
                if (slash < 0) return null;
                var ip = IPAddress.Parse(cidr[..slash]);
                int prefix = int.Parse(cidr[(slash + 1)..]);
                var bytes = ip.GetAddressBytes();
                for (int i = 0; i < bytes.Length; i++)
                {
                    int bits = Math.Max(0, Math.Min(8, prefix - i * 8));
                    bytes[i] &= bits == 0 ? (byte)0x00 : (byte)(0xFF << (8 - bits));
                }
                return new IPNetwork(new IPAddress(bytes), prefix);
            }
            catch { return null; }
        }

        // Factory: opens a connection with an explicit wall-clock timeout.
        // If the timeout fires, arranges disposal of the connection if it eventually arrives.
        public static MikrotikWrapper Connect(string host, string user, string pass, bool useSsl, int port, int timeoutMs)
        {
            Exception? caught = null;
            MikrotikWrapper? wrapper = null;

            var task = Task.Run(() =>
            {
                try { wrapper = new MikrotikWrapper(host, user, pass, useSsl, port); }
                catch (Exception ex) { caught = ex; }
            });

            if (!task.Wait(timeoutMs))
            {
                task.ContinueWith(_ => wrapper?.Dispose());
                throw new TimeoutException("Connection timed out");
            }

            if (caught != null) throw caught;
            return wrapper!;
        }

        // Returns true when retrying with the alternate SSL mode is worth attempting.
        public static bool ShouldTryFallback(Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            string msg = inner.Message;

            // Credentials are wrong regardless of SSL mode.
            if (msg.IndexOf("not logged in",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("invalid user",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("wrong password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("login failure",  StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            // Routing/DNS issues affect both ports equally.
            if (inner is SocketException se)
                return se.SocketErrorCode != SocketError.HostNotFound &&
                       se.SocketErrorCode != SocketError.NetworkUnreachable;

            // Timeout, connection refused, SSL errors → try the other mode.
            return true;
        }

        public void Dispose()
        {
            if (_torchCmd != null)
                _torchCmd.CancelAndJoin(2000);

            _connection.Close();
            _connection.Dispose();
        }
    }
}
