using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using tik4net;
using tiktop.Data;
using tiktop.Helpers;

namespace tiktop
{
    public class MikrotikWrapper: IDisposable
    {
        ITikConnection _connection;
        ITikCommand _torchCmd;

        public MikrotikWrapper(string host, string user, string pass)
        {
            _connection = ConnectionFactory.OpenConnection(TikConnectionType.ApiSsl, host, user, pass);
        }

        public void StartListening(string iface, Action<IReadOnlyDictionary<string, string>> onResponseCallback)
        {
            _torchCmd = _connection.CreateCommandAndParameters("/tool/torch",
               "interface", iface,
               "port", "any",
               "src-address", "0.0.0.0/0",
               "dst-address", "0.0.0.0/0");

            _torchCmd.ExecuteAsync(response =>
            {
                onResponseCallback(response.Words);
            });
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
