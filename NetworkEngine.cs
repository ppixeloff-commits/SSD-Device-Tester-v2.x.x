using System;
using System.Net.NetworkInformation;
using Renci.SshNet;
using System.Threading.Tasks;

namespace SSHTester
{
    public class NetworkEngine
    {
        public static bool PingHost(string ipAddress)
        {
            try
            {
                using Ping pingSender = new Ping();
                PingReply reply = pingSender.Send(ipAddress, 2000);
                return reply.Status == IPStatus.Success;
            }
            catch
            {
                return false;
            }
        }

        public static string ExecuteSshCommand(TestConfig config, string command)
        {
            AuthenticationMethod authMethod;

            if (!string.IsNullOrEmpty(config.Password))
            {
                authMethod = new PasswordAuthenticationMethod(config.User, config.Password);
            }
            else
            {
                // Pokud nemáte klíč, můžete sem dát výchozí logiku nebo vyhodit výjimku
                authMethod = new PasswordAuthenticationMethod(config.User, "");
            }

            ConnectionInfo connectionInfo = new ConnectionInfo(config.Ip, config.User, authMethod)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            using var client = new SshClient(connectionInfo);
            client.Connect();

            using var sshCommand = client.CreateCommand(command);
            sshCommand.CommandTimeout = TimeSpan.FromSeconds(config.SshTimeout);
            string result = sshCommand.Execute();
            
            client.Disconnect();
            return result;
        }
    }
}