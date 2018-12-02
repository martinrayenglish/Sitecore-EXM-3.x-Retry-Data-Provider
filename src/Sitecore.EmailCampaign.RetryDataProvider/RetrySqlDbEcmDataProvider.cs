using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Data.SqlClient;
using System.Linq;

using Newtonsoft.Json;

using Sitecore.Common;
using Sitecore.Configuration;
using Sitecore.Diagnostics;
using Sitecore.EmailCampaign.Model.Dispatch;
using Sitecore.EmailCampaign.Model.Message;
using Sitecore.ExM.Framework.Diagnostics;
using Sitecore.Modules.EmailCampaign.Core;
using Sitecore.Modules.EmailCampaign.Core.Data;
using Sitecore.Modules.EmailCampaign.Core.Dispatch;

namespace Sitecore.EmailCampaign.RetryDataProvider
{
    public class RetrySqlDbEcmDataProvider: SqlDbEcmDataProvider
    {
        protected string DelaySeconds { get; set; }
        protected string RetryCount { get; set; }

        private readonly IMessageStateInfoFactory _messageStateInfoFactory;
        private string _connectionStringName;
        private SqlDbEcmDataProvider _baseDataProvider;
        
        public override void Initialize(string name, NameValueCollection config)
        {
            Assert.ArgumentNotNull(name, nameof(name));
            Assert.ArgumentNotNull(config, nameof(config));

            base.Initialize(name, config);

            _connectionStringName = config.Get("connectionStringName");

            var delaySecondsValue = int.TryParse(DelaySeconds, out var setThreshold) ? setThreshold : Settings.DefaultDelaySeconds;
            var retryCountValue = int.TryParse(RetryCount, out var setRetryCount) ? setRetryCount : Settings.DefaultRetryCount;

            ExecuteRetry.RetryCount = retryCountValue;
            ExecuteRetry.Delay = TimeSpan.FromSeconds(delaySecondsValue);
        }

        public SqlDbEcmDataProvider BaseDataProvider()
        {
            return _baseDataProvider ?? (_baseDataProvider = new ProviderHelper<EcmDataProvider, EcmDataProviderCollection>("ecmDataProvider").Providers["sqlbase"] as SqlDbEcmDataProvider);
        }

        public RetrySqlDbEcmDataProvider() : base(new MessageStateInfoFactory())
        {

        }

        public RetrySqlDbEcmDataProvider(IMessageStateInfoFactory messageStateInfoFactory)
        {
            Assert.ArgumentNotNull(messageStateInfoFactory, nameof(messageStateInfoFactory));

            _messageStateInfoFactory = messageStateInfoFactory;
        }

        public new ILogger Logger { get; set; }

        public override void AddToDispatchQueue(Guid messageId, MessageType messageType, IEnumerable<Tuple<string, DispatchType>> recipients, Dictionary<string, object> customPersonTokens = null)
        {
            Assert.ArgumentNotNull(recipients, nameof(recipients));

            var dispatchQueueItems = recipients.Select(q => new DispatchQueueItem()
            {
                MessageId = messageId,
                RecipientId = q.Item1,
                RecipientQueue = q.Item2 == DispatchType.AbTest ? RecipientQueue.AbTestRecipient : RecipientQueue.Recipient,
                DispatchType = q.Item2,
                LastModified = DateTime.UtcNow,
                MessageType = messageType,
                CustomPersonTokens = customPersonTokens
            });

            if (EngagementAnalyticsPlanSendingContextSwitcher.IsActive)
            {
                var array = dispatchQueueItems.ToArray();
                dispatchQueueItems = array;

                if (array.Length == 1)
                {
                    Switcher<EngagementAnalyticsPlanSendingContext, EngagementAnalyticsPlanSendingContext>.CurrentValue.ExactDispatchQueueElemId = array[0].Id;
                }
            }
            
            ExecuteRetry.Retry(() =>
            {
                using (var connection = new SqlConnection(ConnectionString))
                using (var sqlBulkCopy = new SqlBulkCopy(connection))
                {
                    connection.Open();
                    sqlBulkCopy.DestinationTableName = "DispatchQueue";

                    using (var queueItemDataReader = new DispatchQueueItemDataReader(dispatchQueueItems, this.Logger))
                    {
                        sqlBulkCopy.WriteToServer(queueItemDataReader);
                    }
                }
            });
        }

        public override void ClearDispatchQueue(Guid messageId)
        {
            var connection = new SqlConnection(ConnectionString);
            var command = connection.CreateCommand();

            command.CommandText = "DELETE FROM [DispatchQueue] WHERE [MessageID] = @MessageID";
            command.Parameters.Add(new SqlParameter("@MessageID", messageId));

            ExecuteRetry.Retry(() =>
            {
                using (connection)
                using (command)
                {
                    connection.Open();
                    command.ExecuteNonQuery();
                }
            });
        }

        public override long CountRecipientsInDispatchQueue(Guid messageId, params RecipientQueue[] queueStates)
        {
            var connection = new SqlConnection(ConnectionString);
            var command = connection.CreateCommand();

            command.CommandText = "SELECT COUNT([ID]) FROM [DispatchQueue] WHERE [MessageId] = @MessageId";

            if (queueStates != null && queueStates.Length != 0)
            {
                command.CommandText += $" AND [RecipientQueue] IN ({ string.Join(",", queueStates.Cast<int>().ToArray<int>())})";
            }

            command.Parameters.Add(new SqlParameter("@MessageId", messageId));

            return System.Convert.ToInt64(ExecuteRetry.GetValue<object>(connection, command));
        }

        public override void DeleteRecipientsFromDispatchQueue(Guid queueItemId)
        {
            var connection = new SqlConnection(ConnectionString);
            var command = connection.CreateCommand();

            command.CommandText = "DELETE FROM [DispatchQueue] WHERE [ID] = @ID";
            command.Parameters.Add(new SqlParameter("@ID", queueItemId));

            ExecuteRetry.Retry(() =>
            {
                using (connection)
                using (command)
                {
                    connection.Open();
                    command.ExecuteNonQuery();
                }
            });
        }

        public override IEnumerable<Guid> GetMessagesInProgress(MessageType messageType, TimeSpan timeout)
        {
            var connection = new SqlConnection(ConnectionString);
            var command = connection.CreateCommand();

            command.CommandText = "SELECT DISTINCT([MessageID]) FROM [DispatchQueue] WHERE [MessageType] = @MessageType AND [InProgress] = 1 AND [LastModified] < @LastModified";
            command.Parameters.Add(new SqlParameter("@MessageType", messageType));
            command.Parameters.Add(new SqlParameter("@LastModified", DateTime.UtcNow.Subtract(timeout)));

            var guidList = new List<Guid>();

            ExecuteRetry.Retry(() =>
            {
                using (connection)
                using (command)
                {
                    connection.Open();
                    
                    using (var sqlDataReader = command.ExecuteReader())
                    {
                        while (sqlDataReader.Read())
                        {
                            guidList.Add(sqlDataReader.GetGuid(0));
                        }
                    }
                }
            });

            return guidList;
        }

        public override DispatchQueueItem GetNextRecipientForDispatch(Guid messageId, RecipientQueue queueState)
        {
            var currentValue = Switcher<EngagementAnalyticsPlanSendingContext, EngagementAnalyticsPlanSendingContext>.CurrentValue;
            DispatchQueueItem returnDispatchQueueItem = null;

            var connection = new SqlConnection(ConnectionString);
            var command = connection.CreateCommand();

            command.CommandText = "UPDATE TOP(1) [DispatchQueue] SET [InProgress] = 1, [LastModified] = @LastModified OUTPUT inserted.ID, inserted.MessageID, inserted.RecipientID, inserted.RecipientQueue, inserted.DispatchType, inserted.LastModified, inserted.MessageType, inserted.CustomPersonTokens, inserted.InProgress WHERE [InProgress] = 0";
            command.Parameters.Add(new SqlParameter("@LastModified", DateTime.UtcNow));

            if (currentValue == null)
            {
                command.CommandText += " AND [MessageID] = @MessageID";
                command.Parameters.Add(new SqlParameter("@MessageID", messageId));
                command.CommandText += " AND [RecipientQueue] = @RecipientQueue";
                command.Parameters.Add(new SqlParameter("@RecipientQueue", queueState));
            }
            else
            {
                command.CommandText += " AND [ID] = @ID";
                command.Parameters.Add(new SqlParameter("@ID", currentValue.ExactDispatchQueueElemId));
            }

            ExecuteRetry.Retry(() =>
            {
                using (connection)
                using (command)
                {
                    connection.Open();

                    using (var sqlDataReader = command.ExecuteReader(CommandBehavior.SingleRow))
                    {
                        while (sqlDataReader.Read())
                        {
                            returnDispatchQueueItem = ProcessDispatchQueueItem(sqlDataReader);
                        }
                    }
                }
            });

            return returnDispatchQueueItem;
        }

        public override void ResetProcessState(Guid messageId, TimeSpan? timeout = null)
        {
            var connection = new SqlConnection(ConnectionString);
            var command = connection.CreateCommand();

            command.CommandText = "UPDATE [DispatchQueue] SET [InProgress] = 0, [LastModified] = @LastModified WHERE [MessageID] = @MessageID AND [InProgress] = 1";
            command.Parameters.Add(new SqlParameter("@LastModified", (object)DateTime.UtcNow));
            command.Parameters.Add(new SqlParameter("@MessageID", (object)messageId));

            var nullable = timeout;
            var timeSpan = new TimeSpan();

            if ((nullable.HasValue ? ((nullable.GetValueOrDefault() == timeSpan ? 1 : 0)) : 0) != 0)
            {
                command.CommandText += " AND [LastModified] < @Timeout";

                if (timeout != null)
                {
                    command.Parameters.Add(new SqlParameter("@Timeout", DateTime.UtcNow.Subtract(timeout.Value)));
                }
            }

            ExecuteRetry.Retry(() =>
            {
                using (connection)
                using (command)
                {
                    connection.Open();
                    command.ExecuteNonQuery();
                }
            });
        }

        public override void ChangeDispatchType(Guid messageId, DispatchType sourceType, DispatchType targetType, int itemCount)
        {
            var recipientQueue1 = sourceType == DispatchType.AbTest ? RecipientQueue.AbTestRecipient : RecipientQueue.Recipient;
            var recipientQueue2 = targetType == DispatchType.AbTest ? RecipientQueue.AbTestRecipient : RecipientQueue.Recipient;

            var connection = new SqlConnection(ConnectionString);
            var command = connection.CreateCommand();

            command.CommandText = "UPDATE TOP(@ItemCount) [DispatchQueue] SET [RecipientQueue] = @TargetQueue, [DispatchType] = @TargetType, [LastModified] = @LastModified WHERE [MessageID] = @MessageID AND [RecipientQueue] = @SourceQueue AND [InProgress] = 0";
            command.Parameters.Add(new SqlParameter("@ItemCount", (object)itemCount));
            command.Parameters.Add(new SqlParameter("@TargetQueue", (object)recipientQueue2));
            command.Parameters.Add(new SqlParameter("@TargetType", (object)targetType));
            command.Parameters.Add(new SqlParameter("@LastModified", (object)DateTime.UtcNow));
            command.Parameters.Add(new SqlParameter("@MessageID", (object)messageId));
            command.Parameters.Add(new SqlParameter("@SourceQueue", (object)recipientQueue1));

            ExecuteRetry.Retry(() =>
            {
                using (connection)
                using (command)
                {
                    connection.Open();
                    command.ExecuteNonQuery();
                }
            });
        }

        private static DispatchQueueItem ProcessDispatchQueueItem(SqlDataReader dataReader)
        {
            return new DispatchQueueItem()
            {
                Id = dataReader.GetGuid(0),
                MessageId = dataReader.GetGuid(1),
                RecipientId = dataReader.GetString(2),
                RecipientQueue = (RecipientQueue)dataReader.GetByte(3),
                DispatchType = (DispatchType)dataReader.GetByte(4),
                LastModified = dataReader.GetDateTime(5),
                MessageType = (MessageType)dataReader.GetByte(6),
                CustomPersonTokens = JsonConvert.DeserializeObject<Dictionary<string, object>>(dataReader.IsDBNull(7) ? string.Empty : dataReader.GetString(7)),
                InProgress = dataReader.GetBoolean(8)
            };
        }

        public override CampaignSearchResults SearchCampaigns(CampaignSearchOptions options)
        {   
            return BaseDataProvider().SearchCampaigns(options);
        }
    }
}
