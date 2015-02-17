﻿using System;
using System.Globalization;
using System.Web.SessionState;
using System.Configuration;
using System.Configuration.Provider;
using System.Web.Configuration;
using MongoDB.Driver;
using MongoDB.Bson;
using System.Diagnostics;
using System.IO;
using MongoDB.Driver.Builders;
using System.Web;
using System.Linq;

namespace MongoSessionStateStore
{
    /// <summary>
    /// For further information about parameters see this page in the project wiki: 
    /// https://github.com/MarkCBB/MongoDB-ASP.NET-Session-State-Store/wiki/Web.config-parameters
    /// </summary>
    public sealed class MongoSessionStateStore : SessionStateStoreProviderBase
    {
        private SessionStateSection _config;
        private ConnectionStringSettings _connectionStringSettings;
        private string _applicationName;
        private string _connectionString;
        private bool _writeExceptionsToEventLog;
        internal const string EXCEPTION_MESSAGE = "An exception occurred. Please contact your administrator.";
        internal const string EVENT_SOURCE = "MongoSessionStateStore";
        internal const string EVENT_LOG = "Application";
        private int _maxUpsertAttempts = 220;
        private int _msWaitingForAttempt = 500;
        private bool _autoCreateTTLIndex = true;
        private WriteConcern _writeConcern;

        /// <summary>
        /// The ApplicationName property is used to differentiate sessions
        /// in the data source by application.
        ///</summary>
        public string ApplicationName
        {
            get { return _applicationName; }
        }

        /// <summary>
        /// If false, exceptions are thrown to the caller. If true,
        /// exceptions are written to the event log. 
        /// </summary>
        public bool WriteExceptionsToEventLog
        {
            get { return _writeExceptionsToEventLog; }
            set { _writeExceptionsToEventLog = value; }
        }

        /// <summary>
        /// The max number of attempts that will try to send
        /// an upsert to a replicaSet in case of primary elections.    
        /// </summary>
        public int MaxUpsertAttempts
        {
            get { return _maxUpsertAttempts; }
        }

        /// <summary>
        /// Is the time in milliseconds that will wait between each attempt if
        /// an upsert fails due a primary elections.
        /// </summary>
        public int MsWaitingForAttempt
        {
            get { return _msWaitingForAttempt; }
        }

        public bool AutoCreateTTLIndex
        {
            get { return _autoCreateTTLIndex; }
        }

        public WriteConcern SessionWriteConcern
        {
            get { return _writeConcern; }
        }

        /// <summary>
        /// Returns a reference to the collection in MongoDB that holds the Session state
        /// data.
        /// </summary>
        /// <param name="conn">MongoDB server connection</param>
        /// <returns>MongoCollection</returns>
        private MongoCollection<BsonDocument> GetSessionCollection(MongoServer conn)
        {
            return conn.GetDatabase("SessionState").GetCollection("Sessions");
        }

        /// <summary>
        /// Returns a connection to the MongoDB server holding the session state data.
        /// </summary>
        /// <returns>MongoServer</returns>
        private MongoServer GetConnection()
        {
            var client = new MongoClient(_connectionString);
            return client.GetServer();
        }

        /// <summary>
        /// Initialise the session state store.
        /// </summary>
        /// <param name="name">session state store name. Defaults to "MongoSessionStateStore" if not supplied</param>
        /// <param name="config">configuration settings</param>
        public override void Initialize(string name, System.Collections.Specialized.NameValueCollection config)
        {
            // Initialize values from web.config.
            if (config == null)
                throw new ArgumentNullException("config");

            if (name.Length == 0)
                name = "MongoSessionStateStore";

            if (String.IsNullOrEmpty(config["description"]))
            {
                config.Remove("description");
                config.Add("description", "MongoDB Session State Store provider");
            }

            // Initialize the abstract base class.
            base.Initialize(name, config);

            // Initialize the ApplicationName property.
            _applicationName = System.Web.Hosting.HostingEnvironment.ApplicationVirtualPath;

            // Get <sessionState> configuration element.
            Configuration cfg = WebConfigurationManager.OpenWebConfiguration(ApplicationName);
            _config = (SessionStateSection)cfg.GetSection("system.web/sessionState");

            // Initialize connection string.
            _connectionStringSettings = ConfigurationManager.ConnectionStrings[config["connectionStringName"]];

            if (_connectionStringSettings == null || _connectionStringSettings.ConnectionString.Trim() == "")
            {
                throw new ProviderException("Connection string cannot be blank.");
            }

            _connectionString = _connectionStringSettings.ConnectionString;

            // Initialize WriteExceptionsToEventLog
            _writeExceptionsToEventLog = false;

            if (config["writeExceptionsToEventLog"] != null)
            {
                if (config["writeExceptionsToEventLog"].ToUpper() == "TRUE")
                    _writeExceptionsToEventLog = true;
            }

            bool fsync = false;
            if (config["fsync"] != null)
            {
                if (config["fsync"].ToUpper() == "TRUE")
                    fsync = true;
            }

            int replicasToWrite = 1;
            if (config["replicasToWrite"] != null)
            {
                if (!int.TryParse(config["replicasToWrite"], out replicasToWrite))
                    throw new ProviderException("Replicas To Write must be a valid integer");
            }

            string wValue = "1";
            if (replicasToWrite > 0)
                wValue = (1 + replicasToWrite).ToString(CultureInfo.InvariantCulture);

            _writeConcern = new WriteConcern
            {
                FSync = fsync,
                W = WriteConcern.WValue.Parse(wValue)
            };

            // Initialize maxUpsertAttempts
            _maxUpsertAttempts = 220;
            if (config["maxUpsertAttempts"] != null)
            {
                if (!int.TryParse(config["maxUpsertAttempts"], out _maxUpsertAttempts))
                    throw new Exception("maxUpsertAttempts must be a valid integer");
            }

            //initialize msWaitingForAttempt
            _msWaitingForAttempt = 500;
            if (config["msWaitingForAttempt"] != null)
            {
                if (!int.TryParse(config["msWaitingForAttempt"], out _msWaitingForAttempt))
                    throw new Exception("msWaitingForAttempt must be a valid integer");
            }

            //Initialize AutoCreateTTLIndex
            _autoCreateTTLIndex = true;
            if (config["AutoCreateTTLIndex"] != null)
            {
                if (!bool.TryParse(config["AutoCreateTTLIndex"], out _autoCreateTTLIndex))
                    throw new Exception("AutoCreateTTLIndex must be true or false");
            }

            //Create TTL index if AutoCreateTTLIndex config parameter is true.
            if(_autoCreateTTLIndex)
            {
                var conn = GetConnection();
                var sessionCollection = GetSessionCollection(conn);
                this.CreateTTLIndex(sessionCollection);
            }
        }

        public override SessionStateStoreData CreateNewStoreData(HttpContext context, int timeout)
        {
            return new SessionStateStoreData(new SessionStateItemCollection(),
                SessionStateUtility.GetSessionStaticObjects(context),
                timeout);
        }

        /// <summary>
        /// SessionStateProviderBase.SetItemExpireCallback
        /// </summary>
        public override bool SetItemExpireCallback(SessionStateItemExpireCallback expireCallback)
        {
            return false;
        }

        /// <summary>
        /// Serialize is called by the SetAndReleaseItemExclusive method to 
        /// convert the SessionStateItemCollection into a Base64 string to    
        /// be stored in MongoDB.
        /// </summary>
        private string Serialize(SessionStateItemCollection items)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                if (items != null)
                    items.Serialize(writer);

                writer.Close();

                return Convert.ToBase64String(ms.ToArray());
            }
        }

        /// <summary>
        /// SessionStateProviderBase.SetAndReleaseItemExclusive
        /// </summary>
        public override void SetAndReleaseItemExclusive(HttpContext context,
          string id,
          SessionStateStoreData item,
          object lockId,
          bool newItem)
        {
            BsonArray arraySession = new BsonArray();
            for(int i = 0; i < item.Items.Count; i++)
            {
                string key = item.Items.Keys[i];
                arraySession.Add(new BsonDocument(key, Newtonsoft.Json.JsonConvert.SerializeObject(item.Items[key])));
            }

            MongoServer conn = GetConnection();
            MongoCollection sessionCollection = GetSessionCollection(conn);

            if (newItem)
            {
                var insertDoc = this.GetNewBsonSessionDocument(
                    id: id,
                    applicationName: ApplicationName,
                    created: DateTime.Now.ToUniversalTime(),
                    lockDate: DateTime.Now.ToUniversalTime(),
                    lockId: 0,
                    timeout: item.Timeout,
                    locked: false,
                    jsonSessionItemsArray: arraySession,
                    flags: 0);

                this.UpsertEntireSessionDocument(sessionCollection, insertDoc);
            }
            else
            {
                var query = Query.And(Query.EQ("_id", id), Query.EQ("ApplicationName", ApplicationName), Query.EQ("LockId", (Int32)lockId));
                var update = Update.Set("Expires", DateTime.Now.AddMinutes(item.Timeout).ToUniversalTime());
                update.Set("SessionItemJSON", arraySession);
                update.Set("Locked", false);
                this.UpdateSessionCollection(sessionCollection, query, update);
            }
        }

        /// <summary>
        /// SessionStateProviderBase.GetItem
        /// </summary>
        public override SessionStateStoreData GetItem(HttpContext context,
          string id,
          out bool locked,
          out TimeSpan lockAge,
          out object lockId,
          out SessionStateActions actionFlags)
        {
            return GetSessionStoreItem(false, context, id, out locked,
              out lockAge, out lockId, out actionFlags);
        }

        /// <summary>
        /// SessionStateProviderBase.GetItemExclusive
        /// </summary>
        public override SessionStateStoreData GetItemExclusive(HttpContext context,
          string id,
          out bool locked,
          out TimeSpan lockAge,
          out object lockId,
          out SessionStateActions actionFlags)
        {
            return GetSessionStoreItem(true, context, id, out locked,
              out lockAge, out lockId, out actionFlags);
        }

        /// <summary>
        /// GetSessionStoreItem is called by both the GetItem and 
        /// GetItemExclusive methods. GetSessionStoreItem retrieves the 
        /// session data from the data source. If the lockRecord parameter
        /// is true (in the case of GetItemExclusive), then GetSessionStoreItem
        /// locks the record and sets a new LockId and LockDate.
        /// </summary>
        private SessionStateStoreData GetSessionStoreItem(bool lockRecord,
          HttpContext context,
          string id,
          out bool locked,
          out TimeSpan lockAge,
          out object lockId,
          out SessionStateActions actionFlags)
        {
            // Initial values for return value and out parameters.
            SessionStateStoreData item = null;
            lockAge = TimeSpan.Zero;
            lockId = null;
            locked = false;
            actionFlags = 0;

            MongoServer conn = GetConnection();
            MongoCollection sessionCollection = GetSessionCollection(conn);

            // DateTime to check if current session item is expired.
            // String to hold serialized SessionStateItemCollection.
            BsonArray serializedItems = new BsonArray();
            // True if a record is found in the database.
            bool foundRecord = false;
            // True if the returned session item is expired and needs to be deleted.
            bool deleteData = false;
            // Timeout value from the data store.
            int timeout = 0;


            // lockRecord is true when called from GetItemExclusive and
            // false when called from GetItem.
            // Obtain a lock if possible. Ignore the record if it is expired.
            IMongoQuery query;
            if (lockRecord)
            {
                query = Query.And(Query.EQ("_id", id), Query.EQ("ApplicationName", ApplicationName), Query.EQ("Locked", false), Query.GT("Expires", DateTime.Now.ToUniversalTime()));
                var update = Update.Set("Locked", true);
                update.Set("LockDate", DateTime.Now.ToUniversalTime());
                var result = this.UpdateSessionCollection(sessionCollection, query, update);

                locked = result.DocumentsAffected == 0; // DocumentsAffected == 0 == No record was updated because the record was locked or not found.
            }

            // Retrieve the current session item information.
            query = Query.And(Query.EQ("_id", id), Query.EQ("ApplicationName", ApplicationName));
            var results = this.FindOneSessionItem(sessionCollection, query);

            if (results != null)
            {
                DateTime expires = results["Expires"].ToUniversalTime();

                if (expires < DateTime.Now.ToUniversalTime())
                {
                    // The record was expired. Mark it as not locked.
                    locked = false;
                    // The session was expired. Mark the data for deletion.
                    deleteData = true;
                }
                else
                    foundRecord = true;

                serializedItems = results["SessionItemJSON"].AsBsonArray;
                lockId = results["LockId"].AsInt32;
                lockAge = DateTime.Now.ToUniversalTime().Subtract(results["LockDate"].ToUniversalTime());
                actionFlags = (SessionStateActions)results["Flags"].AsInt32;
                timeout = results["Timeout"].AsInt32;
            }

            // If the returned session item is expired, 
            // delete the record from the data source.
            if (deleteData)
            {
                query = Query.And(Query.EQ("_id", id), Query.EQ("ApplicationName", ApplicationName));
                this.DeleteSessionDocument(sessionCollection, query);
            }

            // The record was not found. Ensure that locked is false.
            if (!foundRecord)
                locked = false;

            // If the record was found and you obtained a lock, then set 
            // the lockId, clear the actionFlags,
            // and create the SessionStateStoreItem to return.
            if (foundRecord && !locked)
            {
                lockId = (int)lockId + 1;

                query = Query.And(Query.EQ("_id", id), Query.EQ("ApplicationName", ApplicationName));
                var update = Update.Set("LockId", (int)lockId);
                update.Set("Flags", 0);
                this.UpdateSessionCollection(sessionCollection, query, update);

                // If the actionFlags parameter is not InitializeItem, 
                // deserialize the stored SessionStateItemCollection.
                item = actionFlags == SessionStateActions.InitializeItem
                    ? CreateNewStoreData(context, (int)_config.Timeout.TotalMinutes)
                    : Deserialize(context, serializedItems, timeout);
            }

            return item;
        }

        private SessionStateStoreData Deserialize(HttpContext context,
         BsonArray serializedItems, int timeout)
        {
            var sessionItems = new SessionStateItemCollection();
            foreach (var value in serializedItems.Values)
            {
                var document = value as BsonDocument;
                string name = document.Names.FirstOrDefault();
                string JSonValues = document.Values.FirstOrDefault().AsString;
                sessionItems[name] = Newtonsoft.Json.JsonConvert.DeserializeObject(JSonValues);
            }

            return new SessionStateStoreData(sessionItems,
              SessionStateUtility.GetSessionStaticObjects(context),
              timeout);
        }

        public override void CreateUninitializedItem(HttpContext context, string id, int timeout)
        {
            MongoServer conn = GetConnection();
            MongoCollection sessionCollection = GetSessionCollection(conn);
            var doc = this.GetNewBsonSessionDocument(
                id: id,
                applicationName: ApplicationName,
                created: DateTime.Now.ToUniversalTime(),
                lockDate: DateTime.Now.ToUniversalTime(),
                lockId: 0,
                timeout: timeout,
                locked: false,
                sessionItems: "",
                jsonSessionItemsArray: new BsonArray(),
                flags: 1);

            this.UpsertEntireSessionDocument(sessionCollection, doc);
        }

        /// <summary>
        /// This is a helper function that writes exception detail to the 
        /// event log. Exceptions are written to the event log as a security
        /// measure to ensure private database details are not returned to 
        /// browser. If a method does not return a status or Boolean
        /// indicating the action succeeded or failed, the caller also 
        /// throws a generic exception.
        /// </summary>
        private void WriteToEventLog(Exception e, string action)
        {
            using (var log = new EventLog())
            {
                log.Source = EVENT_SOURCE;
                log.Log = EVENT_LOG;

                string message =
                  String.Format("An exception occurred communicating with the data source.\n\nAction: {0}\n\nException: {1}",
                  action, e);

                log.WriteEntry(message);
            }
        }

        public override void Dispose()
        {
        }

        public override void EndRequest(HttpContext context)
        {

        }

        public override void InitializeRequest(HttpContext context)
        {

        }

        public override void ReleaseItemExclusive(HttpContext context, string id, object lockId)
        {
            MongoServer conn = GetConnection();
            MongoCollection sessionCollection = GetSessionCollection(conn);

            var query = Query.And(Query.EQ("_id", id), Query.EQ("ApplicationName", ApplicationName), Query.EQ("LockId", (Int32)lockId));
            var update = Update.Set("Locked", false);
            update.Set("Expires", DateTime.Now.AddMinutes(_config.Timeout.TotalMinutes).ToUniversalTime());

            this.UpdateSessionCollection(sessionCollection, query, update);
        }

        public override void RemoveItem(HttpContext context, string id, object lockId, SessionStateStoreData item)
        {
            MongoServer conn = GetConnection();
            MongoCollection sessionCollection = GetSessionCollection(conn);

            var query = Query.And(Query.EQ("_id", id), Query.EQ("ApplicationName", ApplicationName), Query.EQ("LockId", (Int32)lockId));

            this.DeleteSessionDocument(sessionCollection, query);
        }

        public override void ResetItemTimeout(HttpContext context, string id)
        {
            MongoServer conn = GetConnection();
            MongoCollection sessionCollection = GetSessionCollection(conn);
            var query = Query.And(Query.EQ("_id", id), Query.EQ("ApplicationName", ApplicationName));
            var update = Update.Set("Expires", DateTime.Now.AddMinutes(_config.Timeout.TotalMinutes).ToUniversalTime());

            this.UpdateSessionCollection(sessionCollection, query, update);
        }        
    }
}
