# Sitecore EXM 3.x Retry Data Provider
The module is an improved version of the EXM 3.x SQL Data Provider that has the ability to detect SQL deadlock victims and retry transactions for specified period of time.

### Why would you use it?

If you are using EXM 3.x to send large email campaigns (1 million+ per day), chances are that you have had it pause many times midsend and couldn't understand why.

The pausing is caused by SQL deadlocks due to the massive amount of activity on the EXM SQL databases. Tweaking configurations and throwing hardware against the issue will not solve the problem.

### Sample Exception
Checking your EXM logs will reveal an error similar to this:

```
ERROR Transaction (Process ID 116) was deadlocked on lock | communication buffer resources with another process and has been chosen as the deadlock victim. Rerun the transaction.
Exception: System.Data.SqlClient.SqlException
Message: Transaction (Process ID 116) was deadlocked on lock | communication buffer resources with another process and has been chosen as the deadlock victim. Rerun the transaction.
Source: .Net SqlClient Data Provider
   at System.Data.SqlClient.SqlConnection.OnError(SqlException exception, Boolean breakConnection, Action`1 wrapCloseInAction)
   at System.Data.SqlClient.TdsParser.ThrowExceptionAndWarning(TdsParserStateObject stateObj, Boolean callerHasConnectionLock, Boolean asyncClose)
   at System.Data.SqlClient.TdsParser.TryRun(RunBehavior runBehavior, SqlCommand cmdHandler, SqlDataReader dataStream, BulkCopySimpleResultSet bulkCopyHandler, TdsParserStateObject stateObj, Boolean& dataReady)
   at System.Data.SqlClient.SqlDataReader.TryHasMoreRows(Boolean& moreRows)
   at System.Data.SqlClient.SqlDataReader.TryReadInternal(Boolean setTimeout, Boolean& more)
   at System.Data.SqlClient.SqlDataReader.Read()
   at System.Data.SqlClient.SqlCommand.CompleteExecuteScalar(SqlDataReader ds, Boolean returnSqlValue)
   at System.Data.SqlClient.SqlCommand.ExecuteScalar()
   at Sitecore.Modules.EmailCampaign.Core.Data.SqlDbEcmDataProvider.CountRecipientsInDispatchQueue(Guid messageId, RecipientQueue[] queueStates)
   at Sitecore.Modules.EmailCampaign.Core.Gateways.DefaultEcmDataGateway.CountRecipientsInDispatchQueue(Guid messageId, RecipientQueue[] queueStates)
   at Sitecore.Modules.EmailCampaign.Core.Analytics.MessageStatistics.get_Unprocessed()
   at Sitecore.Modules.EmailCampaign.Core.Analytics.MessageStatistics.get_Processed()
   at Sitecore.Modules.EmailCampaign.Core.MessageStateInfo.InitializeSendingState()
   at Sitecore.Modules.EmailCampaign.Core.MessageStateInfo.InitializeMessageStateInfo()
   at Sitecore.Modules.EmailCampaign.Factory.GetMessageStateInfo(String messageItemId, String contextLanguage)
   at Sitecore.EmailCampaign.Server.Services.MessageInfoService.Get(String messageId, String contextLanguage)
   at Sitecore.EmailCampaign.Server.Controllers.MessageInfo.MessageInfoController.MessageInfo(MessageInfoContext data)

```

### How does it work?
This data provider introduces efficient SQL deadlock handling. When a deadlock is detected, it will wait 5 seconds and then retry the transaction. The code will try to execute a deadlocked transaction 3 times. 

## Configuration
Defaults are set to wait 5 seconds for the retry, and the max retry attempts is 3. The DelaySeconds and RetryCount settings can be modified to suit your needs.

```
<configuration xmlns:patch="http://www.sitecore.net/xmlconfig/">
  <sitecore>
    <ecmDataProvider defaultProvider="sqlretry">
      <providers>
        <clear/>
        <add name="sqlretry" type="Sitecore.EmailCampaign.RetryDataProvider.RetrySqlDbEcmDataProvider, Sitecore.EmailCampaign.RetryDataProvider" connectionStringName="exm.master">
          <Logger type="Sitecore.ExM.Framework.Diagnostics.Logger, Sitecore.ExM.Framework" factoryMethod="get_Instance"/>
          <DelaySeconds>5</DelaySeconds>
          <RetryCount>3</RetryCount>
        </add>
        <add name="sqlbase" type="Sitecore.Modules.EmailCampaign.Core.Data.SqlDbEcmDataProvider, Sitecore.EmailCampaign" connectionStringName="exm.master">
          <Logger type="Sitecore.ExM.Framework.Diagnostics.Logger, Sitecore.ExM.Framework" factoryMethod="get_Instance"/>
        </add>
      </providers>
    </ecmDataProvider>
  </sitecore>
</configuration>
```

## Installation

The Sitecore package located in the Package folder called Sitecore EXM Retry DP-1.0 contains:

* Binary (release build).
* Configuration file.

Use the Sitecore Installation Wizard to install the package. 
