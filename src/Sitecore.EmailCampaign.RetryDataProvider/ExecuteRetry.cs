using System;
using System.Data.SqlClient;
using System.Threading;

using Sitecore.Diagnostics;

namespace Sitecore.EmailCampaign.RetryDataProvider
{
    public class ExecuteRetry
    {
        public static TimeSpan Delay;
        public static int RetryCount;

        private static T Retry<T>(Func<T> func)
        {
            while (true)
            {
                try
                {   
                    return func();
                }
                catch (SqlException e)
                {
                    --RetryCount;

                    if (RetryCount <= 0)
                    {
                        throw;
                    }

                    switch (e.Number)
                    {
                        case 1205:
                            Log.Warn("[Sitecore.EmailCampaign.RetryDataProvider] Retry: Deadlock detected, retrying...", e);
                            break;
                        case -2:
                            Log.Warn("[Sitecore.EmailCampaign.RetryDataProvider] Retry: Timeout detected, retrying...", e);
                            break;
                        default:
                            throw;
                    }

                    Thread.Sleep(Delay);
                }
            }
        }

        public static void Retry(Action action)
        {
            Retry(() => { action(); return true; });
        }

        public static T GetValue<T>(SqlConnection connection, SqlCommand command)
        {
            return Retry(() => {
                using (connection)
                using (command)
                {
                    connection.Open();

                    var value = command.ExecuteScalar();
                    if (value is DBNull) return default(T);
                    return (T)value;
                }
            });
        }
    }
}
