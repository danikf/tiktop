using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
            _connection = ConnectionFactory.OpenConnection(connType, host, port, user, pass);
        }

        public void StartListening(string iface, Action<ToolTorch> onItemCallback, Action<Exception>? onError = null)
        {
            _torchCmd = _connection.LoadAsync<ToolTorch>(
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
                .Where(a => !a.Disabled && !a.Invalid)
                .Select(a => ParseNetwork(a.Address))
                .OfType<IPNetwork>()
                .ToList();
        }

        private static IPNetwork? ParseNetwork(string cidr)
        {
            try
            {
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

        public void Dispose()
        {
            if (_torchCmd != null)
                _torchCmd.CancelAndJoin(2000);

            _connection.Close();
            _connection.Dispose();
        }
    }
}
