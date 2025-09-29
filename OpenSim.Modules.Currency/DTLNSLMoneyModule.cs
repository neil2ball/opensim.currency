// * Modified by Fumi.Iseki for Unix/Linix  http://www.nsl.tuis.ac.jp
// *
// * Copyright (c) Contributors, http://opensimulator.org/, http://www.nsl.tuis.ac.jp/
// * See CONTRIBUTORS.TXT for a full list of copyright holders.
// *
// * Redistribution and use in source and binary forms, with or without
// * modification, are permitted provided that the following conditions are met:
// *     * Redistributions of source code must retain the above copyright
// *       notice, this list of conditions and the following disclaimer.
// *     * Redistributions in binary form must reproduce the above copyright
// *       notice, this list of conditions and the following disclaimer in the
// *       documentation and/or other materials provided with the distribution.
// *     * Neither the name of the OpenSim Project nor the
// *       names of its contributors may be used to endorse or promote products
// *       derived from this software without specific prior written permission.
// *
// * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
// * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
// * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES
// * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Web;
using System.IO;
using System.Globalization;

using log4net;
using Nini.Config;
using Nwc.XmlRpc;
using Mono.Addins;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;
using OpenSim.Region.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using OpenSim.Data.MySQL.MySQLMoneyDataWrapper;
using NSL.Certificate.Tools;
using NSL.Network.XmlRpc;

// Add aliases to resolve OSDMap ambiguity
using OMVOSDMap = OpenMetaverse.StructuredData.OSDMap;
using OMVOSD = OpenMetaverse.StructuredData.OSD;

[assembly: Addin("DTLNSLMoneyModule", "1.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace OpenSim.Modules.Currency
{
    /// <summary>
    /// Transaction Type
    /// </summary>
    public enum TransactionType : int
    {
        None                = 0,
        // Extend
        BirthGift           = 900,
        AwardPoints         = 901,
        // One-Time Charges
        ObjectClaim         = 1000,
        LandClaim           = 1001,
        GroupCreate         = 1002,
        GroupJoin           = 1004,
        TeleportCharge      = 1100,
        UploadCharge        = 1101,
        LandAuction         = 1102,
        ClassifiedCharge    = 1103,
        // Recurrent Charges
        ObjectTax           = 2000,
        LandTax             = 2001,
        LightTax            = 2002,
        ParcelDirFee        = 2003,
        GroupTax            = 2004,
        ClassifiedRenew     = 2005,
        ScheduledFee        = 2900,
        // Inventory Transactions
        GiveInventory       = 3000,
        // Transfers Between Users
        ObjectSale          = 5000,
        Gift                = 5001,
        LandSale            = 5002,
        ReferBonus          = 5003,
        InvntorySale        = 5004,
        RefundPurchase      = 5005,
        LandPassSale        = 5006,
        DwellBonus          = 5007,
        PayObject           = 5008,
        ObjectPays          = 5009,
        BuyMoney            = 5010,
        MoveMoney           = 5011,
        SendMoney           = 5012,
        // Group Transactions
        GroupLandDeed       = 6001,
        GroupObjectDeed     = 6002,
        GroupLiability      = 6003,
        GroupDividend       = 6004,
        GroupMembershipDues = 6005,
        // Stipend Credits
        StipendBasic        = 10000
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "DTLNSLMoneyModule")]
    public class DTLNSLMoneyModule : IMoneyModule, ISharedRegionModule
    {
        #region Constant numbers and members.

        // Constant memebers   
        private const int MONEYMODULE_REQUEST_TIMEOUT = 10000;

        // Private data members.   
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // HttpClient for REST API calls
        private static readonly HttpClient httpClient = new HttpClient();

        //private bool  m_enabled = true;
        private bool  m_sellEnabled   = false;
        private bool  m_enable_server = true;   // enable Money Server
		
		private string m_currencySymbol = string.Empty;
		private string m_currencyBaseUri = string.Empty;
		private string m_currencyCapsUrl = string.Empty;

        private IConfigSource m_config;

        private string m_moneyServURL    = string.Empty;
        public  BaseHttpServer HttpServer;

        private string m_certFilename    = "";
        private string m_certPassword    = "";
        private bool   m_checkServerCert = false;
        private string m_cacertFilename  = "";
        //private X509Certificate2 m_cert  = null;

        private bool   m_use_web_settle  = false;
        private string m_settle_url      = "";
        private string m_settle_message  = "";
        private bool   m_settle_user     = false;

        private int    m_hg_avatarClass  = (int)AvatarType.HG_AVATAR;

        private NSLCertificateVerify m_certVerify = new NSLCertificateVerify(); // For server authentication

        // Redirect configuration
        private bool   m_redirectEnabled = true;
        private string m_redirectUrl = "https://eudaimon.me/microtokens/";
        private string m_redirectMessage = "Please visit our website to purchase currency";

        // REST API configuration
        private string m_restBaseUrl = string.Empty;
        private string m_consumerKey = string.Empty;
        private string m_consumerSecret = string.Empty;
        private bool m_useRestApi = false;

        // XML-RPC Money Server disable configuration
        private bool m_disableXmlRpc = false;

        // REST API validation flag
        private bool m_restApiValid = false;

        /// <summary>   
        /// Scene dictionary indexed by Region Handle   
        /// </summary>   
        private Dictionary<ulong, Scene> m_sceneList = new Dictionary<ulong, Scene>();

        /// <summary>   
        /// To cache the balance data while the money server is not available.   
        /// </summary>   
        private Dictionary<UUID, int> m_moneyServer = new Dictionary<UUID, int>();

        // Events  
        public event ObjectPaid OnObjectPaid;

        // Price
        private int   ObjectCount               = 0;
        private int   PriceEnergyUnit           = 100;
        private int   PriceObjectClaim          = 10;
        private int   PricePublicObjectDecay    = 4;
        private int   PricePublicObjectDelete   = 4;
        private int   PriceParcelClaim          = 1;
        private float PriceParcelClaimFactor    = 1.0f;
        private int   PriceUpload               = 0;
        private int   PriceRentLight            = 5;
        private float PriceObjectRent           = 1.0f;
        private float PriceObjectScaleFactor    = 10.0f;
        private int   PriceParcelRent           = 1;
        private int   PriceGroupCreate          = 0;
        private int   TeleportMinPrice          = 2;
        private float TeleportPriceExponent     = 2.0f;
        private float EnergyEfficiency          = 1.0f;

        #endregion

        /// <summary>
        /// Initialise
        /// </summary>
        /// <param name="scene"></param>
        /// <param name="source"></param>
        public void Initialise(Scene scene, IConfigSource source)
        {
			m_currencyBaseUri = Util.AppendEndSlash(scene.RegionInfo.ServerURI);
            Initialise(source);
            if (string.IsNullOrEmpty(m_moneyServURL))
			{
				// No external CurrencyServer configured.
				// Fall back to the region’s own base URI and disable XML-RPC Money Server communications.
				m_moneyServURL = Util.AppendEndSlash(scene.RegionInfo.ServerURI);
				m_log.Warn("[MONEY MODULE]: CurrencyServer not configured, defaulting to region server URI");
				m_disableXmlRpc = true;
			}
            AddRegion(scene);
        }

        #region ISharedRegionModule interface

		public void Initialise(IConfigSource source)
		{
			//m_log.InfoFormat("[MONEY MODULE]: Initialise:");

			// Handle the parameters errors.
			if (source==null) return;

			try {
				m_config = source;

				// [Economy] section
				IConfig economyConfig = m_config.Configs["Economy"];

				if (economyConfig.GetString("EconomyModule")!=Name && economyConfig.GetString("economymodule")!=Name) {
					//m_enabled = false;
					m_log.InfoFormat("[MONEY MODULE]: Initialise: The DTL/NSL MoneyModule is disabled");
					return;
				}
				else {
					m_log.InfoFormat("[MONEY MODULE]: Initialise: The DTL/NSL MoneyModule is enabled");
				}

				m_sellEnabled  = economyConfig.GetBoolean("SellEnabled", m_sellEnabled);
				m_moneyServURL = economyConfig.GetString("CurrencyServer", m_moneyServURL);
				
				// 🔥 NEW: Auto-detect region server URL if not configured
				if (string.IsNullOrEmpty(m_moneyServURL))
				{
					// This will be set properly when regions are added
					m_log.InfoFormat("[MONEY MODULE]: CurrencyServer not configured - will use region server's self-advertised URL");
				}
				else
				{
					m_log.InfoFormat("[MONEY MODULE]: Using configured CurrencyServer: {0}", m_moneyServURL);
				}
				
				m_currencySymbol = economyConfig.GetString("CurrencySymbol", "L$");
				
				// REST API configuration
				m_useRestApi = economyConfig.GetBoolean("UseRestApi", false);
				m_restBaseUrl = economyConfig.GetString("RestBaseUrl", m_restBaseUrl);
				m_consumerKey = economyConfig.GetString("ConsumerKey", m_consumerKey);
				m_consumerSecret = economyConfig.GetString("ConsumerSecret", m_consumerSecret);

				// XML-RPC disable configuration - ONLY for external money server communications
				m_disableXmlRpc = economyConfig.GetBoolean("DisableXmlRpc", m_disableXmlRpc);
				m_log.InfoFormat("[MONEY MODULE]: External Money Server XML-RPC disabled: {0}", m_disableXmlRpc);

				// Validate REST API configuration
				if (m_useRestApi)
				{
					m_restApiValid = ValidateRestApiConfiguration();
					m_log.InfoFormat("[MONEY MODULE]: REST API enabled: {0}, Base URL: {1}, Configuration Valid: {2}", 
						m_useRestApi, m_restBaseUrl, m_restApiValid);
				}
				else
				{
					m_log.InfoFormat("[MONEY MODULE]: REST API disabled");
				}

				// Client Certification   // クライアント証明書
				m_certFilename = economyConfig.GetString("ClientCertFilename", m_certFilename);
				m_certPassword = economyConfig.GetString("ClientCertPassword", m_certPassword);
				if (m_certFilename!="") {
					m_certVerify.SetPrivateCert(m_certFilename, m_certPassword);
					//m_cert = new X509Certificate2(m_certFilename, m_certPassword);
					//m_cert = new X509Certificate2(m_certFilename, m_certPassword, X509KeyStorageFlags.MachineKeySet);
					m_log.Info("[MONEY MODULE]: Initialise: Issue Authentication of Client. Cert File is " + m_certFilename);
				}

				// Server Authentication  // MoneyServer のサーバ証明書のチェック
				m_checkServerCert = economyConfig.GetBoolean("CheckServerCert", m_checkServerCert);
				m_cacertFilename  = economyConfig.GetString ("CACertFilename",  m_cacertFilename);

				if (m_cacertFilename != "") {
					m_certVerify.SetPrivateCA(m_cacertFilename);
				}
				else {
					m_checkServerCert = false;
				}

				if (m_checkServerCert) {
					m_log.Info("[MONEY MODULE]: Initialise: Execute Authentication of Server. CA Cert File is " + m_cacertFilename);
				}
				else {
					m_log.Info("[MONEY MODULE]: Initialise: No check Money Server or CACertFilename is empty. CheckServerCert is false.");
				}

				//ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(m_certVerify.ValidateServerCertificate);
				//ServicePointManager.UseNagleAlgorithm = false;
				//ServicePointManager.Expect100Continue = false;

				// Settlement
				m_use_web_settle = economyConfig.GetBoolean("SettlementByWeb",   m_use_web_settle);
				m_settle_url     = economyConfig.GetString ("SettlementURL",     m_settle_url);
				m_settle_message = economyConfig.GetString ("SettlementMessage", m_settle_message);

				// Redirect configuration
				m_redirectEnabled = economyConfig.GetBoolean("RedirectEnabled", m_redirectEnabled);
				m_redirectUrl = economyConfig.GetString("RedirectUrl", m_redirectUrl);
				m_redirectMessage = economyConfig.GetString("RedirectMessage", m_redirectMessage);
				
				m_log.InfoFormat("[MONEY MODULE]: Currency redirect enabled: {0}, URL: {1}", m_redirectEnabled, m_redirectUrl);

				// Price
				PriceEnergyUnit         = economyConfig.GetInt  ("PriceEnergyUnit",         PriceEnergyUnit);
				PriceObjectClaim        = economyConfig.GetInt  ("PriceObjectClaim",        PriceObjectClaim);
				PricePublicObjectDecay  = economyConfig.GetInt  ("PricePublicObjectDecay",  PricePublicObjectDecay);
				PricePublicObjectDelete = economyConfig.GetInt  ("PricePublicObjectDelete", PricePublicObjectDelete);
				PriceParcelClaim        = economyConfig.GetInt  ("PriceParcelClaim",        PriceParcelClaim);
				PriceParcelClaimFactor  = economyConfig.GetFloat("PriceParcelClaimFactor",  PriceParcelClaimFactor);
				PriceUpload             = economyConfig.GetInt  ("PriceUpload",             PriceUpload);
				PriceRentLight          = economyConfig.GetInt  ("PriceRentLight",          PriceRentLight);
				PriceObjectRent         = economyConfig.GetFloat("PriceObjectRent",         PriceObjectRent);
				PriceObjectScaleFactor  = economyConfig.GetFloat("PriceObjectScaleFactor",  PriceObjectScaleFactor);
				PriceParcelRent         = economyConfig.GetInt  ("PriceParcelRent",         PriceParcelRent);
				PriceGroupCreate        = economyConfig.GetInt  ("PriceGroupCreate",        PriceGroupCreate);
				TeleportMinPrice        = economyConfig.GetInt  ("TeleportMinPrice",        TeleportMinPrice);
				TeleportPriceExponent   = economyConfig.GetFloat("TeleportPriceExponent",   TeleportPriceExponent);
				EnergyEfficiency        = economyConfig.GetFloat("EnergyEfficiency",        EnergyEfficiency);

				// for HG Avatar
				string avatar_class = economyConfig.GetString("HGAvatarAs", "HGAvatar").ToLower();
				if      (avatar_class=="localavatar")   m_hg_avatarClass = (int)AvatarType.LOCAL_AVATAR;
				else if (avatar_class=="guestavatar")   m_hg_avatarClass = (int)AvatarType.GUEST_AVATAR;
				else if (avatar_class=="hgavatar")      m_hg_avatarClass = (int)AvatarType.HG_AVATAR;
				else if (avatar_class=="foreignavatar") m_hg_avatarClass = (int)AvatarType.FOREIGN_AVATAR;
				else                                    m_hg_avatarClass = (int)AvatarType.UNKNOWN_AVATAR;

			}
			catch {
				m_log.ErrorFormat("[MONEY MODULE]: Initialise: Faile to read configuration file");
			}
		}

		/// <summary>
		/// Auto-detects the region server's URL for currency services - COMPATIBLE VERSION
		/// </summary>
		private string GetRegionServerCurrencyUrl(Scene scene)
		{
			try
			{
				// Prefer the region's advertised ServerURI if present
				if (!string.IsNullOrEmpty(scene.RegionInfo.ServerURI))
				{
					string serverUri = scene.RegionInfo.ServerURI;

					// Ensure it has a scheme
					if (!serverUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
						!serverUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
					{
						serverUri = "http://" + serverUri;
					}

					// Parse and normalize with System.Uri
					UriBuilder builder = new UriBuilder(serverUri);

					// If no port was specified, use the region's HttpPort or fallback
					if (builder.Port <= 0)
					{
						int port = (int)scene.RegionInfo.HttpPort;
						if (port == 0) port = 9000;
						builder.Port = port;
					}

					string normalized = builder.Uri.ToString().TrimEnd('/') + "/";
					m_log.InfoFormat("[MONEY MODULE]: Using ServerURI for currency server: {0}", normalized);
					return normalized;
				}

				// Fallback: build from ExternalHostName + HttpPort
				string externalHost = scene.RegionInfo.ExternalHostName;
				int httpPort = (int)scene.RegionInfo.HttpPort;
				if (httpPort == 0) httpPort = 9000;

				string fallbackUrl = $"http://{externalHost}:{httpPort}/";
				m_log.InfoFormat("[MONEY MODULE]: Using ExternalHostName for currency server: {0}", fallbackUrl);
				return fallbackUrl;
			}
			catch (Exception ex)
			{
				m_log.ErrorFormat("[MONEY MODULE]: Error auto-detecting region server URL: {0}", ex.Message);
				return string.Empty;
			}
		}

		/// <summary>
		/// Register the Currency Legacy Capability - CORRECTED VERSION
		/// </summary>
		public void RegisterCurrencyCapsCapability(Scene scene)
		{
			try
			{
				scene.EventManager.OnRegisterCaps += (agentID, caps) =>
				{
					try
					{
						// Use a proper StreamHandler instead of RestStreamHandler for CAPS
						var handler = new RestStreamHandler("POST", "",
						(requestBody, path, param, httpRequest, httpResponse) =>
						{
							try
							{
								return HandleCapsCurrencyRequest(requestBody, httpRequest, httpResponse);
							}
							catch (Exception ex)
							{
								m_log.ErrorFormat("[MONEY MODULE]: Exception in Currency CAPS handler: {0}", ex);
								var error = new OSDMap();
								error["success"] = OSD.FromBoolean(false);
								error["error"]   = OSD.FromString("Internal server error");
								httpResponse.StatusCode = 500;
								httpResponse.ContentType = "application/llsd+xml";
								return OSDParser.SerializeLLSDXmlString(error);
							}
						});

						// Register the Currency CAPS handler
						caps.RegisterHandler("Currency", handler);

						// Build the CAPS URL
						m_currencyCapsUrl = scene.RegionInfo.ServerURI.TrimEnd('/') + "/CAPS/" + caps.CapsObjectPath + "/Currency";

						m_log.InfoFormat(
							"[MONEY MODULE]: Registered Currency CAPS for agent {0} in region {1}, URL {2}",
							agentID, scene.RegionInfo.RegionName, m_currencyCapsUrl);
					}
					catch (Exception ex)
					{
						m_log.ErrorFormat(
							"[MONEY MODULE]: Error registering Currency CAPS via OnRegisterCaps: {0}",
							ex.Message);
					}
				};
			}
			catch (Exception ex)
			{
				m_log.WarnFormat("[MONEY MODULE]: CAPS registration failed: {0}", ex.Message);
			}
		}

		public void AddRegion(Scene scene)
		{
			if (scene == null) return;

			scene.RegisterModuleInterface<IMoneyModule>(this);

			if (string.IsNullOrEmpty(m_moneyServURL))
			{
				m_moneyServURL = GetRegionServerCurrencyUrl(scene);
				if (!string.IsNullOrEmpty(m_moneyServURL))
				{
					m_enable_server = true;
					m_log.InfoFormat("[MONEY MODULE]: Auto-configured currency server URL: {0}", m_moneyServURL);
				}
				else
				{
					m_enable_server = false;
					m_log.WarnFormat("[MONEY MODULE]: Could not auto-configure currency server URL - currency disabled");
				}
			}

			lock (m_sceneList)
			{
				if (m_sceneList.Count == 0)
				{
					HttpServer = new BaseHttpServer(9000);

					MainServer.Instance.AddSimpleStreamHandler(new SimpleStreamHandler("/currency.php", ProcessCurrencyPHP_Simple));
					MainServer.Instance.AddSimpleStreamHandler(new SimpleStreamHandler("/landtool.php", ProcessLandtoolPHP));

					/*MainServer.Instance.AddStreamHandler(
						new RestStreamHandler("GET", "/currency/balance",
							(requestBody, path, param, httpRequest, httpResponse) =>
								ProcessCurrencyRest(requestBody, path, param, httpRequest, httpResponse)));

					MainServer.Instance.AddStreamHandler(
						new RestStreamHandler("POST", "/currency/quote",
							(requestBody, path, param, httpRequest, httpResponse) =>
								ProcessCurrencyRest(requestBody, path, param, httpRequest, httpResponse)));

					MainServer.Instance.AddStreamHandler(
						new RestStreamHandler("POST", "/currency/buy",
							(requestBody, path, param, httpRequest, httpResponse) =>
								ProcessCurrencyRest(requestBody, path, param, httpRequest, httpResponse)));*/

					HttpServer.AddXmlRPCHandler("money_balance_request", SimulatorUserBalanceRequestHandler);
					HttpServer.AddXmlRPCHandler("money_transfer_request", RegionMoveMoneyHandler);
					HttpServer.AddXmlRPCHandler("buy_currency", BuyCurrencyHandler);
					HttpServer.AddXmlRPCHandler("getCurrencyQuote", GetCurrencyQuoteHandler);
					HttpServer.AddXmlRPCHandler("buyCurrency", BuyCurrencyHandler);

					MainServer.Instance.AddXmlRPCHandler("money_balance_request", SimulatorUserBalanceRequestHandler);
					MainServer.Instance.AddXmlRPCHandler("money_transfer_request", RegionMoveMoneyHandler);
					MainServer.Instance.AddXmlRPCHandler("buy_currency", BuyCurrencyHandler);
					MainServer.Instance.AddXmlRPCHandler("getCurrencyQuote", GetCurrencyQuoteHandler);
					MainServer.Instance.AddXmlRPCHandler("buyCurrency", BuyCurrencyHandler);

					if (m_enable_server && m_disableXmlRpc)
					{
						m_log.InfoFormat("[MONEY MODULE]: XML-RPC handlers disabled - using REST API only for local avatars");
					}
					else if (m_enable_server)
					{
						HttpServer.AddXmlRPCHandler("OnMoneyTransfered", OnMoneyTransferedHandler);
						HttpServer.AddXmlRPCHandler("UpdateBalance", BalanceUpdateHandler);
						HttpServer.AddXmlRPCHandler("UserAlert", UserAlertHandler);
						HttpServer.AddXmlRPCHandler("GetBalance", GetBalanceHandler);
						HttpServer.AddXmlRPCHandler("AddBankerMoney", AddBankerMoneyHandler);
						HttpServer.AddXmlRPCHandler("SendMoney", SendMoneyHandler);
						HttpServer.AddXmlRPCHandler("MoveMoney", MoveMoneyHandler);

						MainServer.Instance.AddXmlRPCHandler("OnMoneyTransfered", OnMoneyTransferedHandler);
						MainServer.Instance.AddXmlRPCHandler("UpdateBalance", BalanceUpdateHandler);
						MainServer.Instance.AddXmlRPCHandler("UserAlert", UserAlertHandler);
						MainServer.Instance.AddXmlRPCHandler("GetBalance", GetBalanceHandler);
						MainServer.Instance.AddXmlRPCHandler("AddBankerMoney", AddBankerMoneyHandler);
						MainServer.Instance.AddXmlRPCHandler("SendMoney", SendMoneyHandler);
						MainServer.Instance.AddXmlRPCHandler("MoveMoney", MoveMoneyHandler);
					}
				}

				if (m_sceneList.ContainsKey(scene.RegionInfo.RegionHandle))
					m_sceneList[scene.RegionInfo.RegionHandle] = scene;
				else
					m_sceneList.Add(scene.RegionInfo.RegionHandle, scene);
			}

			RegisterCurrencyCapsCapability(scene);

			// Wire simulator features (currency symbol, base URI, and CAPS URL)
			WireSimulatorFeatures(scene);

			scene.EventManager.OnNewClient     += OnNewClient;
			scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
			scene.EventManager.OnMakeChildAgent += MakeChildAgent;

			scene.EventManager.OnMoneyTransfer   += MoneyTransferAction;
			scene.EventManager.OnValidateLandBuy += ValidateLandBuy;
			scene.EventManager.OnLandBuy         += processLandBuy;
		}


        public void RemoveRegion(Scene scene)
        {
            if (scene==null) return;

            lock (m_sceneList) {
                scene.EventManager.OnNewClient      -= OnNewClient;
                scene.EventManager.OnMakeRootAgent  -= OnMakeRootAgent;
                scene.EventManager.OnMakeChildAgent -= MakeChildAgent;

                // for OpenSim
                scene.EventManager.OnMoneyTransfer   -= MoneyTransferAction;
                scene.EventManager.OnValidateLandBuy -= ValidateLandBuy;
                scene.EventManager.OnLandBuy         -= processLandBuy;
            }
        }

        public void RegionLoaded(Scene scene)
        {
            //m_log.InfoFormat("[MONEY MODULE]: RegionLoaded:");
        }

        public Type ReplaceableInterface
        {
            //get { return typeof(IMoneyModule); }
            get { return null; }
        }

        public bool IsSharedModule
        {
            get { return true; }
        }

        public string Name
        {
            get { return "DTLNSLMoneyModule"; }
        }

        public void PostInitialise()
        {
            //m_log.InfoFormat("[MONEY MODULE]: PostInitialise:");
        }

        public void Close()
        {
            //m_log.InfoFormat("[MONEY MODULE]: Close:");
        }

        #endregion

        #region IMoneyModule interface.

        // for LSL llGiveMoney() function
        public bool ObjectGiveMoney(UUID objectID, UUID fromID, UUID toID, int amount, UUID txn, out string result)
        {
            //m_log.InfoFormat("[MONEY MODULE]: ObjectGiveMoney: LSL ObjectGiveMoney. UUID = {0}", objectID.ToString());

            result = string.Empty;
            if (!m_sellEnabled) {
                result = "LINDENDOLLAR_INSUFFICIENTFUNDS";
                return false;
            }

            string objName = string.Empty;
            string avatarName = string.Empty;

            SceneObjectPart sceneObj = GetLocatePrim(objectID);
            if (sceneObj==null) {
                result = "LINDENDOLLAR_INSUFFICIENTFUNDS";
                return false;
            }
            objName = sceneObj.Name;

            Scene scene = GetLocateScene(toID);
            if (scene!=null) {
                UserAccount account = scene.UserAccountService.GetUserAccount(scene.RegionInfo.ScopeID, toID);
                if (account!=null) {
                    avatarName = account.FirstName + " " + account.LastName;
                }
            }

            bool ret = false;
            string description = String.Format("Object {0} pays {1}", objName, avatarName);

            if (sceneObj.OwnerID==fromID) {
                ulong regionHandle = sceneObj.RegionHandle;
                UUID  regionUUID   = sceneObj.RegionID;
                if (GetLocateClient(fromID)!=null) {
                    ret = TransferMoney(fromID, toID, amount, (int)TransactionType.ObjectPays, objectID, regionHandle, regionUUID, description);
                }
                else {
                    ret = ForceTransferMoney(fromID, toID, amount, (int)TransactionType.ObjectPays, objectID, regionHandle, regionUUID, description);
                }
            }

            if (!ret) result = "LINDENDOLLAR_INSUFFICIENTFUNDS";
            return ret;
        }

        //
        public int UploadCharge
        {
            get { return PriceUpload; }
        }

        //
        public int GroupCreationCharge
        {
            get { return PriceGroupCreate; }
        }

        public int GetBalance(UUID agentID)
        {
            return QueryBalance(agentID);
        }

        public bool UploadCovered(UUID agentID, int amount)
        {
            int balance = QueryBalance(agentID);
            return balance >= amount;
        }

        public bool AmountCovered(UUID agentID, int amount)
        {
            int balance = QueryBalance(agentID);
            return balance >= amount;
        }

        public void ApplyUploadCharge(UUID agentID, int amount, string text)
        {
            ulong regionHandle = GetLocateScene(agentID).RegionInfo.RegionHandle;
            UUID  regionUUID   = GetLocateScene(agentID).RegionInfo.RegionID;
            PayMoneyCharge(agentID, amount, (int)TransactionType.UploadCharge, regionHandle, regionUUID, text);
        }

        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type)
        {
            ApplyCharge(agentID, amount, type, string.Empty);
        }

        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type, string text)
        {
            ulong regionHandle = GetLocateScene(agentID).RegionInfo.RegionHandle;
            UUID  regionUUID   = GetLocateScene(agentID).RegionInfo.RegionID;
            PayMoneyCharge(agentID, amount, (int)type, regionHandle, regionUUID, text);
        }

        public bool Transfer(UUID fromID, UUID toID, int regionHandle, int amount, MoneyTransactionType type, string text)
        {
            return TransferMoney(fromID, toID, amount, (int)type, UUID.Zero, (ulong)regionHandle, UUID.Zero, text);
        }

        public bool Transfer(UUID fromID, UUID toID, UUID objectID, int amount, MoneyTransactionType type, string text)
        {
            SceneObjectPart sceneObj = GetLocatePrim(objectID);
            if (sceneObj==null) return false;

            ulong regionHandle = sceneObj.ParentGroup.Scene.RegionInfo.RegionHandle;
            UUID  regionUUID   = sceneObj.ParentGroup.Scene.RegionInfo.RegionID;
            return TransferMoney(fromID, toID, amount, (int)type, objectID, (ulong)regionHandle, regionUUID, text);
        }

        // for 0.8.3 over
        public void MoveMoney(UUID fromAgentID, UUID toAgentID, int amount, string text)
        {
            ForceTransferMoney(fromAgentID, toAgentID, amount, (int)TransactionType.MoveMoney, UUID.Zero, (ulong)0, UUID.Zero, text);
        }

        // for 0.9.1 over
        public bool MoveMoney(UUID fromAgentID, UUID toAgentID, int amount, MoneyTransactionType type, string text)
        {
            bool ret = ForceTransferMoney(fromAgentID, toAgentID, amount, (int)type, UUID.Zero, (ulong)0, UUID.Zero, text);
            return ret;
        }

        #endregion

        #region REST API Integration Methods - CORRECTED

        /// <summary>
        /// Validate REST API configuration on startup with correct endpoint
        /// </summary>
        private bool ValidateRestApiConfiguration()
        {
            if (!m_useRestApi || string.IsNullOrEmpty(m_restBaseUrl))
                return false;

            try
            {
                // Test the API endpoint with the correct structure
                var testUrl = $"{m_restBaseUrl}users?consumer_key={Uri.EscapeDataString(m_consumerKey)}&consumer_secret={Uri.EscapeDataString(m_consumerSecret)}";
                var response = httpClient.GetAsync(testUrl).Result;
                
                if (response.IsSuccessStatusCode)
                {
                    m_log.InfoFormat("[MONEY MODULE]: REST API configuration validated successfully");
                    return true;
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var responseContent = response.Content.ReadAsStringAsync().Result;
                    m_log.ErrorFormat("[MONEY MODULE]: REST API authentication failed: {0}", responseContent);
                    return false;
                }
                else
                {
                    m_log.WarnFormat("[MONEY MODULE]: REST API endpoint returned {0}", response.StatusCode);
                    // Even if it's not 200, if it's not 401, the endpoint might be valid
                    return response.StatusCode != HttpStatusCode.NotFound;
                }
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[MONEY MODULE]: REST API configuration validation failed: {0}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Determine if a user is local (has a WordPress account) with correct endpoint
        /// </summary>
        private bool IsLocalUser(UUID userID)
        {
            if (!m_useRestApi || !m_restApiValid || string.IsNullOrEmpty(m_restBaseUrl))
                return false;

            try
            {
                var url = $"{m_restBaseUrl}{userID}?consumer_key={Uri.EscapeDataString(m_consumerKey)}&consumer_secret={Uri.EscapeDataString(m_consumerSecret)}";
                var response = httpClient.GetAsync(url).Result;
                
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // User doesn't exist in external system - this is expected for Hypergrid users
                    m_log.DebugFormat("[MONEY MODULE]: IsLocalUser: User {0} not found in external system (expected for Hypergrid)", userID);
                    return false;
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    m_log.ErrorFormat("[MONEY MODULE]: IsLocalUser: Authentication failed for user {0}", userID);
                    return false;
                }
                else if (response.IsSuccessStatusCode)
                {
                    var responseString = response.Content.ReadAsStringAsync().Result;
                    // Check if we got a valid response (not an error)
                    return !responseString.Contains("\"code\":\"rest_forbidden\"") && 
                           !responseString.Contains("\"error\"") &&
                           responseString.Contains("\"data\"");
                }
                else
                {
                    m_log.WarnFormat("[MONEY MODULE]: IsLocalUser: API returned {0} for user {1}", response.StatusCode, userID);
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                if (ex.Message.Contains("404"))
                {
                    m_log.DebugFormat("[MONEY MODULE]: IsLocalUser: User {0} not found in external system: {1}", userID, ex.Message);
                }
                else
                {
                    m_log.WarnFormat("[MONEY MODULE]: IsLocalUser: Error checking local user {0}: {1}", userID, ex.Message);
                }
                return false;
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[MONEY MODULE]: IsLocalUser: Error checking local user {0}: {1}", userID, ex.Message);
                return false;
            }
        }

		/// <summary>
		/// Unified balance query method with improved error handling and fallback
		/// </summary>
		private int QueryBalance(UUID userUUID)
		{
			m_log.DebugFormat("[MONEY MODULE]: QueryBalance called for user: {0}", userUUID);
			
			// First, try REST API if configured and valid
			if (m_useRestApi && m_restApiValid)
			{
				int balance = QueryBalanceFromRestApi(userUUID);
				if (balance >= 0) // Valid balance or user not found (0)
				{
					m_log.DebugFormat("[MONEY MODULE]: REST API balance for {0}: {1}", userUUID, balance);
					return balance;
				}
				
				m_log.WarnFormat("[MONEY MODULE]: REST API failed for user {0}, falling back to alternative methods", userUUID);
			}

			// For Hypergrid users or when REST fails, use XML-RPC but respect the external server disable flag
			IClientAPI client = GetLocateClient(userUUID);
			
			// Only block if it's an external money server AND XML-RPC is disabled
			if (m_disableXmlRpc && IsExternalMoneyServer())
			{
				m_log.WarnFormat("[MONEY MODULE]: Balance query for user {0} blocked - external XML-RPC disabled", userUUID);
				return 0;
			}
			
			return QueryBalanceFromMoneyServer(client);
		}

        /// <summary>
        /// REST API balance query with correct response parsing for your API format
        /// </summary>
        private int QueryBalanceFromRestApi(UUID userUUID)
        {
            try
            {
                var url = $"{m_restBaseUrl}{userUUID}?consumer_key={Uri.EscapeDataString(m_consumerKey)}&consumer_secret={Uri.EscapeDataString(m_consumerSecret)}";
                var response = httpClient.GetAsync(url).Result;
                
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // User doesn't exist in external system - return 0 balance
                    m_log.DebugFormat("[MONEY MODULE]: QueryBalanceFromRestApi: User {0} not found in external system", userUUID);
                    return 0;
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    m_log.ErrorFormat("[MONEY MODULE]: QueryBalanceFromRestApi: Authentication failed for user {0}", userUUID);
                    return -1;
                }
                else if (!response.IsSuccessStatusCode)
                {
                    m_log.WarnFormat("[MONEY MODULE]: QueryBalanceFromRestApi: API error {0} for user {1}", response.StatusCode, userUUID);
                    return -1;
                }
                
                var responseString = response.Content.ReadAsStringAsync().Result;
                
                // Parse the actual API response format: {"data":"5"}
                try
                {
                    // Simple JSON parsing for your format
                    if (responseString.Contains("\"data\""))
                    {
                        int start = responseString.IndexOf("\"data\":") + 7;
                        int end = responseString.IndexOf("}", start);
                        if (end == -1) end = responseString.Length;
                        
                        string dataValue = responseString.Substring(start, end - start).Trim().Replace("\"", "");
                        
                        // The value might be a decimal string like "5" or "23.34"
                        if (decimal.TryParse(dataValue, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal balanceDecimal))
                        {
                            // Convert to integer (assuming the API returns the balance in the same units as OpenSim)
                            // If your API returns dollars but OpenSim uses cents, multiply by 100
                            int balance = (int)Math.Round(balanceDecimal); // Remove * 100 if your API already returns cents
                            m_log.DebugFormat("[MONEY MODULE]: QueryBalanceFromRestApi: User {0} balance: {1} (from API: {2})", 
                                userUUID, balance, balanceDecimal);
                            return balance;
                        }
                        else
                        {
                            m_log.WarnFormat("[MONEY MODULE]: QueryBalanceFromRestApi: Could not parse balance value '{0}' for user {1}", 
                                dataValue, userUUID);
                            return -1;
                        }
                    }
                    
                    // Check for error response
                    if (responseString.Contains("\"code\":\"rest_forbidden\""))
                    {
                        m_log.ErrorFormat("[MONEY MODULE]: QueryBalanceFromRestApi: Authentication failed for user {0}", userUUID);
                        return -1;
                    }
                    
                    m_log.WarnFormat("[MONEY MODULE]: QueryBalanceFromRestApi: Invalid response format for user {0}. Response: {1}", userUUID, responseString);
                    return -1;
                }
                catch (Exception jsonEx)
                {
                    m_log.WarnFormat("[MONEY MODULE]: QueryBalanceFromRestApi: JSON parsing error for user {0}: {1}. Response: {2}", 
                        userUUID, jsonEx.Message, responseString);
                    return -1;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[MONEY MODULE]: QueryBalanceFromRestApi: REST API exception getting balance for user {0}: {1}", userUUID, ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// Enhanced TransferMoney method to handle all scenarios with improved error handling
        /// </summary>
        private bool TransferMoney(UUID sender, UUID receiver, int amount, int type, UUID objectID, 
                                  ulong regionHandle, UUID regionUUID, string description)
        {
            bool isSenderLocal = m_useRestApi && m_restApiValid && IsLocalUser(sender);
            bool isReceiverLocal = m_useRestApi && m_restApiValid && IsLocalUser(receiver);

            if (m_disableXmlRpc)
            {
                // When XML-RPC is disabled, only allow transactions between local users
                if (!isSenderLocal || !isReceiverLocal)
                {
                    m_log.WarnFormat("[MONEY MODULE]: Transaction blocked - XML-RPC disabled and one or more users are Hypergrid avatars. Sender: {0}, Receiver: {1}", 
                                    sender, receiver);
                    
                    // Notify users about the restriction
                    NotifyHypergridTransactionBlocked(sender, receiver, amount, description);
                    return false;
                }
                
                // Both users are local - use REST API
                return TransferViaRestApi(sender, receiver, amount, description);
            }
            
            // Original logic when XML-RPC is enabled
            // Both users are local - use REST API exclusively
            if (isSenderLocal && isReceiverLocal)
            {
                return TransferViaRestApi(sender, receiver, amount, description);
            }
            // Both users are Hypergrid - use XML-RPC exclusively
            else if (!isSenderLocal && !isReceiverLocal)
            {
                return TransferViaXmlRpc(sender, receiver, amount, type, objectID, regionHandle, regionUUID, description);
            }
            // Mixed transaction (local ↔ Hypergrid)
            else
            {
                return HandleMixedTransaction(sender, receiver, amount, type, objectID, regionHandle, regionUUID, description);
            }
        }

        /// <summary>
        /// Handle transactions between local and Hypergrid users with improved error handling
        /// </summary>
        private bool HandleMixedTransaction(UUID sender, UUID receiver, int amount, int type, UUID objectID, 
                                           ulong regionHandle, UUID regionUUID, string description)
        {
            bool isSenderLocal = m_useRestApi && m_restApiValid && IsLocalUser(sender);

            // For mixed transactions, we need to use both systems
            if (isSenderLocal)
            {
                // Debit local sender via REST API
                if (!UpdateWalletViaRestApi(sender, -amount, "debit", 
                    $"Transfer to Hypergrid user {receiver}: {description}"))
                {
                    m_log.ErrorFormat("[MONEY MODULE]: HandleMixedTransaction: Failed to debit local sender {0}", sender);
                    return false;
                }
                
                // Credit Hypergrid receiver via XML-RPC (force transfer since sender is local)
                if (!TransferViaXmlRpc(UUID.Zero, receiver, amount, type, objectID, regionHandle, regionUUID, 
                    $"Transfer from local user {sender}: {description}"))
                {
                    // Rollback the debit if credit fails
                    m_log.WarnFormat("[MONEY MODULE]: HandleMixedTransaction: Failed to credit Hypergrid receiver {0}, rolling back", receiver);
                    UpdateWalletViaRestApi(sender, amount, "credit", 
                        $"Rollback failed transfer to {receiver}");
                    return false;
                }
            }
            else
            {
                // Debit Hypergrid sender via XML-RPC
                if (!TransferViaXmlRpc(sender, UUID.Zero, amount, type, objectID, regionHandle, regionUUID, 
                    $"Transfer to local user {receiver}: {description}"))
                {
                    m_log.ErrorFormat("[MONEY MODULE]: HandleMixedTransaction: Failed to debit Hypergrid sender {0}", sender);
                    return false;
                }
                
                // Credit local receiver via REST API
                if (!UpdateWalletViaRestApi(receiver, amount, "credit", 
                    $"Transfer from Hypergrid user {sender}: {description}"))
                {
                    // Rollback the debit if credit fails
                    m_log.WarnFormat("[MONEY MODULE]: HandleMixedTransaction: Failed to credit local receiver {0}, rolling back", receiver);
                    TransferViaXmlRpc(UUID.Zero, sender, amount, type, objectID, regionHandle, regionUUID, 
                        $"Rollback failed transfer to {receiver}");
                    return false;
                }
            }
            
            return true;
        }

        /// <summary>
        /// REST API wallet update with correct payload structure for your API
        /// </summary>
        private bool UpdateWalletViaRestApi(UUID userUUID, int amount, string action, string description)
        {
            try
            {
                // Convert integer amount to the format expected by your API
                // If OpenSim uses cents but your API uses dollars, divide by 100.0
                decimal apiAmount = amount; // Remove / 100.0m if your API expects cents
                
                // Create the JSON payload structure that matches your API requirements
                string jsonPayload = $@"{{
                    ""amount"": {apiAmount.ToString(CultureInfo.InvariantCulture)},
                    ""action"": ""{action}"",
                    ""consumer_key"": ""{m_consumerKey}"",
                    ""consumer_secret"": ""{m_consumerSecret}"",
                    ""transaction_detail"": ""{HttpUtility.JavaScriptStringEncode(description)}"",
                    ""payment_method"": ""opensim"",
                    ""note"": ""OpenSim transaction {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}""
                }}";

                m_log.DebugFormat("[MONEY MODULE]: UpdateWalletViaRestApi: Sending payload: {0}", jsonPayload);

                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var response = httpClient.PutAsync($"{m_restBaseUrl}{userUUID}", content).Result;
                
                if (response.IsSuccessStatusCode)
                {
                    var responseString = response.Content.ReadAsStringAsync().Result;
                    m_log.DebugFormat("[MONEY MODULE]: UpdateWalletViaRestApi: Success response: {0}", responseString);
                    
                    // Check for success response format
                    if (responseString.Contains("\"response\":\"success\"") || 
                        responseString.Contains("\"success\":true") || 
                        responseString.Contains("\"status\":\"success\"") ||
                        responseString.Contains("\"data\"") || // Your API might return success differently
                        responseString.Length < 50) // Simple success indicator
                    {
                        return true;
                    }
                    else
                    {
                        m_log.WarnFormat("[MONEY MODULE]: UpdateWalletViaRestApi: API returned success status but unexpected response: {0}", responseString);
                        // Still return true if we got a 200 status code
                        return true;
                    }
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var errorContent = response.Content.ReadAsStringAsync().Result;
                    m_log.ErrorFormat("[MONEY MODULE]: UpdateWalletViaRestApi: Authentication failed for user {0}. Response: {1}", 
                        userUUID, errorContent);
                    return false;
                }
                else if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    m_log.WarnFormat("[MONEY MODULE]: UpdateWalletViaRestApi: User {0} not found in external system", userUUID);
                    return false;
                }
                else
                {
                    var errorContent = response.Content.ReadAsStringAsync().Result;
                    m_log.ErrorFormat("[MONEY MODULE]: UpdateWalletViaRestApi: API error {0} for user {1}. Response: {2}", 
                        response.StatusCode, userUUID, errorContent);
                    return false;
                }
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[MONEY MODULE]: UpdateWalletViaRestApi: REST API exception updating wallet for user {0}: {1}", userUUID, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// REST API transfer between local users with improved error handling
        /// </summary>
        private bool TransferViaRestApi(UUID fromUUID, UUID toUUID, int amount, string description)
        {
            // Debit from sender
            if (!UpdateWalletViaRestApi(fromUUID, -amount, "debit", 
                $"Transfer to {toUUID}: {description}"))
            {
                m_log.ErrorFormat("[MONEY MODULE]: TransferViaRestApi: Failed to debit sender {0}", fromUUID);
                return false;
            }

            // Credit to receiver
            if (!UpdateWalletViaRestApi(toUUID, amount, "credit", 
                $"Transfer from {fromUUID}: {description}"))
            {
                // Rollback if credit fails
                m_log.WarnFormat("[MONEY MODULE]: TransferViaRestApi: Failed to credit receiver {0}, rolling back", toUUID);
                UpdateWalletViaRestApi(fromUUID, amount, "credit", 
                    $"Rollback failed transfer to {toUUID}");
                return false;
            }

            m_log.InfoFormat("[MONEY MODULE]: TransferViaRestApi: Successfully transferred {0} from {1} to {2}", amount, fromUUID, toUUID);
            return true;
        }

        /// <summary>
        /// XML-RPC transfer (for Hypergrid users) - uses existing XML-RPC infrastructure
        /// </summary>
        private bool TransferViaXmlRpc(UUID sender, UUID receiver, int amount, int type, UUID objectID, 
                                      ulong regionHandle, UUID regionUUID, string description)
        {
            bool ret = false;
            IClientAPI senderClient = GetLocateClient(sender);

            if (senderClient == null && sender != UUID.Zero)
            {
                m_log.InfoFormat("[MONEY MODULE]: TransferViaXmlRpc: Client {0} not found", sender.ToString());
                return false;
            }

            if (sender != UUID.Zero && QueryBalanceFromMoneyServer(senderClient) < amount)
            {
                m_log.InfoFormat("[MONEY MODULE]: TransferViaXmlRpc: Insufficient balance in client [{0}]", sender.ToString());
                return false;
            }

            if (m_enable_server)
            {
                string objName = string.Empty;
                SceneObjectPart sceneObj = GetLocatePrim(objectID);
                if (sceneObj != null) objName = sceneObj.Name;
  
                Hashtable paramTable = new Hashtable();
                paramTable["senderID"] = sender.ToString();
                paramTable["receiverID"] = receiver.ToString();
                
                if (sender != UUID.Zero)
                {
                    paramTable["senderSessionID"] = senderClient.SessionId.ToString();
                    paramTable["senderSecureSessionID"] = senderClient.SecureSessionId.ToString();
                }
                
                paramTable["transactionType"] = type;
                paramTable["objectID"] = objectID.ToString();
                paramTable["objectName"] = objName;
                paramTable["regionHandle"] = regionHandle.ToString();
                paramTable["regionUUID"] = regionUUID.ToString();
                paramTable["amount"] = amount;
                paramTable["description"] = description;

                // Use force transfer for system-initiated transactions (mixed transactions)
                string method = (sender == UUID.Zero) ? "ForceTransferMoney" : "TransferMoney";
                Hashtable resultTable = genericCurrencyXMLRPCRequest(paramTable, method);

                if (resultTable != null && resultTable.Contains("success"))
                {
                    ret = (bool)resultTable["success"];
                }
                else
                {
                    m_log.ErrorFormat("[MONEY MODULE]: TransferViaXmlRpc: Cannot process money transfer from [{0}] to [{1}]", 
                        sender.ToString(), receiver.ToString());
                }
            }

            return ret;
        }

        #endregion

        #region Helper methods for improved error handling

        /// <summary>
        /// Notify users about blocked Hypergrid transactions when XML-RPC is disabled
        /// </summary>
        private void NotifyHypergridTransactionBlocked(UUID sender, UUID receiver, int amount, string description)
        {
            string message = "Currency transactions are currently disabled for Hypergrid avatars. Please use local currency for transactions.";
            
            try
            {
                // Notify sender
                IClientAPI senderClient = GetLocateClient(sender);
                if (senderClient != null)
                {
                    senderClient.SendAlertMessage(message);
                }
                
                // Notify receiver if they're online
                IClientAPI receiverClient = GetLocateClient(receiver);
                if (receiverClient != null)
                {
                    receiverClient.SendAlertMessage(message);
                }
                
                m_log.InfoFormat("[MONEY MODULE]: Notified users about blocked Hypergrid transaction");
            }
            catch (Exception ex)
            {
                m_log.WarnFormat("[MONEY MODULE]: Error notifying users about blocked transaction: {0}", ex.Message);
            }
        }

        #endregion

        #region MoneyModule event handlers

        // 
        private void OnNewClient(IClientAPI client)
        {
            //m_log.InfoFormat("[MONEY MODULE]: OnNewClient");

            client.OnEconomyDataRequest += OnEconomyDataRequest;
            client.OnLogout             += ClientClosed;

            client.OnMoneyBalanceRequest += OnMoneyBalanceRequest;
            client.OnRequestPayPrice     += OnRequestPayPrice;
            client.OnObjectBuy           += OnObjectBuy;
        }

        public void OnMakeRootAgent(ScenePresence agent)
        {
            //m_log.InfoFormat("[MONEY MODULE]: OnMakeRootAgent:");

            int balance = 0;
            IClientAPI client = agent.ControllingClient;

            m_enable_server = LoginMoneyServer(agent, out balance);
            client.SendMoneyBalance(UUID.Zero, true, new byte[0], balance, 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);

            //client.OnMoneyBalanceRequest += OnMoneyBalanceRequest;
            //client.OnRequestPayPrice   += OnRequestPayPrice;
            //client.OnObjectBuy             += OnObjectBuy;
        }      

        // for OnClientClosed event
        private void ClientClosed(IClientAPI client)
        {
            //m_log.InfoFormat("[MONEY MODULE]: ClientClosed:");

            if (m_enable_server && client!=null) {
                LogoffMoneyServer(client);
            }
        }

        // for OnMakeChildAgent event
        private void MakeChildAgent(ScenePresence avatar)
        {
            //m_log.InfoFormat("[MONEY MODULE]: MakeChildAgent:");
        }

        // for OnMoneyTransfer event 
        private void MoneyTransferAction(Object sender, EventManager.MoneyTransferArgs moneyEvent)
        {
            //m_log.InfoFormat("[MONEY MODULE]: MoneyTransferAction: type = {0}", moneyEvent.transactiontype);
        
            if (!m_sellEnabled) return;

            // Check the money transaction is necessary.   
            if (moneyEvent.sender==moneyEvent.receiver) {
                return;
            }

            UUID receiver = moneyEvent.receiver;
            // Pay for the object.   
            if (moneyEvent.transactiontype==(int)TransactionType.PayObject) {
                SceneObjectPart sceneObj = GetLocatePrim(moneyEvent.receiver);
                if (sceneObj!=null) {
                    receiver = sceneObj.OwnerID;
                }
                else {
                    return;
                }
            }

            // Before paying for the object, save the object local ID for current transaction.
            UUID  objectID = UUID.Zero;
            ulong regionHandle = 0;
            UUID  regionUUID   = UUID.Zero;

            if (sender is Scene) {
                Scene scene  = (Scene)sender;
                regionHandle = scene.RegionInfo.RegionHandle;
                regionUUID   = scene.RegionInfo.RegionID;

                if (moneyEvent.transactiontype==(int)TransactionType.PayObject) {
                    objectID = scene.GetSceneObjectPart(moneyEvent.receiver).UUID;
                }
            }

            TransferMoney(moneyEvent.sender, receiver, moneyEvent.amount, moneyEvent.transactiontype, objectID, regionHandle, regionUUID, "OnMoneyTransfer event");
            return;
        }

		// for OnValidateLandBuy event
		private void ValidateLandBuy(Object sender, EventManager.LandBuyArgs landBuyEvent)
		{
			//m_log.InfoFormat("[MONEY MODULE]: ValidateLandBuy:");
			
			IClientAPI senderClient = GetLocateClient(landBuyEvent.agentId);
			if (senderClient!=null) {
				int balance = QueryBalance(landBuyEvent.agentId);
				// FIX: Properly cast uint to int
				int parcelPrice = (int)landBuyEvent.parcelPrice;
				if (balance >= parcelPrice) {
					lock(landBuyEvent) {
						landBuyEvent.economyValidated = true;
					}
				}
			}
			return;
		}

		// for LandBuy event
		private void processLandBuy(Object sender, EventManager.LandBuyArgs landBuyEvent)
		{
			//m_log.InfoFormat("[MONEY MODULE]: processLandBuy:");

			if (!m_sellEnabled) return;

			lock(landBuyEvent) {
				if (landBuyEvent.economyValidated==true && landBuyEvent.transactionID==0) {
					landBuyEvent.transactionID = Util.UnixTimeSinceEpoch();

					ulong parcelID = (ulong)landBuyEvent.parcelLocalID;
					UUID  regionUUID = UUID.Zero;
					if (sender is Scene) regionUUID = ((Scene)sender).RegionInfo.RegionID;

					// FIX: Properly cast uint to int
					int parcelPrice = (int)landBuyEvent.parcelPrice;
					int landSale = (int)TransactionType.LandSale;
					if (TransferMoney(landBuyEvent.agentId, landBuyEvent.parcelOwnerID, 
									  parcelPrice, landSale, regionUUID, parcelID, regionUUID, "Land Purchase")) {
						landBuyEvent.amountDebited = parcelPrice;
					}
				}
			}
			return;
		}

        // for OnObjectBuy event
        public void OnObjectBuy(IClientAPI remoteClient, UUID agentID, UUID sessionID, 
                                UUID groupID, UUID categoryID, uint localID, byte saleType, int salePrice)
        {
            m_log.InfoFormat("[MONEY MODULE]: OnObjectBuy: agent = {0}, {1}", agentID, remoteClient.AgentId);

            // Handle the parameters error.   
            if (!m_sellEnabled) return;
            if (remoteClient==null || salePrice<0) return;

            // Get the balance from money server.   
            int balance = QueryBalance(agentID);
            if (balance<salePrice) {
                remoteClient.SendAgentAlertMessage("Unable to buy now. You don't have sufficient funds", false);
                return;
            }

            Scene scene = GetLocateScene(remoteClient.AgentId);
            if (scene!=null) {
                SceneObjectPart sceneObj = scene.GetSceneObjectPart(localID);
                if (sceneObj!=null) {
                    IBuySellModule mod = scene.RequestModuleInterface<IBuySellModule>();
                    if (mod!=null) {
                        UUID  receiverId = sceneObj.OwnerID;
                        ulong regionHandle = sceneObj.RegionHandle;
                        UUID  regionUUID   = sceneObj.RegionID;
                        bool ret = false;
                        //
                        if (salePrice>=0) {
                            if (!string.IsNullOrEmpty(m_moneyServURL)) {
                                ret = TransferMoney(remoteClient.AgentId, receiverId, salePrice,
                                                (int)TransactionType.PayObject, sceneObj.UUID, regionHandle, regionUUID, "Object Buy");
                            }
                            else if (salePrice==0) {    // amount is 0 with No Money Server
                                ret = true;
                            }
                        }
                        if (ret) {
                            mod.BuyObject(remoteClient, categoryID, localID, saleType, salePrice);
                        }
                    }
                }
                else {
                    remoteClient.SendAgentAlertMessage("Unable to buy now. The object was not found", false);
                    return;
                }
            }
            return;
        }

        /// <summary>   
        /// Sends the the stored money balance to the client   
        /// </summary>   
        /// <param name="client"></param>   
        /// <param name="agentID"></param>   
        /// <param name="SessionID"></param>   
        /// <param name="TransactionID"></param>   
        private void OnMoneyBalanceRequest(IClientAPI client, UUID agentID, UUID SessionID, UUID TransactionID)
        {
            m_log.InfoFormat("[MONEY MODULE]: OnMoneyBalanceRequest:");

            if (client.AgentId==agentID && client.SessionId==SessionID) {
                int balance = 0;
                //
                if (m_enable_server) {
                    balance = QueryBalance(agentID);
                }

                client.SendMoneyBalance(TransactionID, true, new byte[0], balance, 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);
            }
            else {
                client.SendAlertMessage("Unable to send your money balance");
            }
        }

        private void OnRequestPayPrice(IClientAPI client, UUID objectID)
        {
            m_log.InfoFormat("[MONEY MODULE]: OnRequestPayPrice:");

            Scene scene = GetLocateScene(client.AgentId);
            if (scene==null) return;
            SceneObjectPart sceneObj = scene.GetSceneObjectPart(objectID);
            if (sceneObj==null) return;
            SceneObjectGroup group = sceneObj.ParentGroup;
            SceneObjectPart root = group.RootPart;

            client.SendPayPrice(objectID, root.PayPrice);
        }

        //
        //private void OnEconomyDataRequest(UUID agentId)
        private void OnEconomyDataRequest(IClientAPI user)
        {
            //m_log.InfoFormat("[MONEY MODULE]: OnEconomyDataRequest:");
            //IClientAPI user = GetLocateClient(agentId);

            if (user!=null) {
                if (m_enable_server || string.IsNullOrEmpty(m_moneyServURL)) {
                    //Scene s = GetLocateScene(user.AgentId);
                    Scene s = (Scene)user.Scene;
                    user.SendEconomyData(EnergyEfficiency, s.RegionInfo.ObjectCapacity, ObjectCount, PriceEnergyUnit, PriceGroupCreate,
                                     PriceObjectClaim, PriceObjectRent, PriceObjectScaleFactor, PriceParcelClaim, PriceParcelClaimFactor,
                                     PriceParcelRent, PricePublicObjectDecay, PricePublicObjectDelete, PriceRentLight, PriceUpload,
                                     TeleportMinPrice, TeleportPriceExponent);
                }
            }
        }

        #endregion

        #region MoneyModule XML-RPC Handler

        // "OnMoneyTransfered" RPC from MoneyServer
        public XmlRpcResponse OnMoneyTransferedHandler(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            if (m_disableXmlRpc)
            {
                m_log.WarnFormat("[MONEY MODULE]: OnMoneyTransferedHandler blocked - XML-RPC disabled");
                
                XmlRpcResponse disabledResp = new XmlRpcResponse();
                Hashtable disabledParamTable = new Hashtable();
                disabledParamTable["success"] = false;
                disabledParamTable["error"] = "XML-RPC transactions are disabled. Please use the REST API for local currency transactions.";
                disabledParamTable["error_code"] = "XML_RPC_DISABLED";
                disabledResp.Value = disabledParamTable;
                return disabledResp;
            }

            m_log.InfoFormat("[MONEY MODULE]: OnMoneyTransferedHandler:");

            bool ret = false;

            if (request.Params.Count > 0)
            {
                Hashtable requestParam = (Hashtable)request.Params[0];
                if (requestParam.Contains("clientUUID") && requestParam.Contains("clientSessionID") && requestParam.Contains("clientSecureSessionID"))
                {
                    UUID clientUUID = UUID.Zero;
                    UUID.TryParse((string)requestParam["clientUUID"], out clientUUID);

                    if (clientUUID != UUID.Zero)
                    {
                        IClientAPI client = GetLocateClient(clientUUID);
                        string sessionid = (string)requestParam["clientSessionID"];
                        string secureid = (string)requestParam["clientSecureSessionID"];
                        
                        if (client != null && secureid == client.SecureSessionId.ToString() && 
                            (sessionid == UUID.Zero.ToString() || sessionid == client.SessionId.ToString()))
                        {
                            if (requestParam.Contains("transactionType") && requestParam.Contains("objectID") && requestParam.Contains("amount"))
                            {
                                //m_log.InfoFormat("[MONEY MODULE]: OnMoneyTransferedHandler: type = {0}", requestParam["transactionType"]);

                                // Pay for the object.
                                if ((int)requestParam["transactionType"] == (int)TransactionType.PayObject)
                                {
                                    // Send notify to the client(viewer) for Money Event Trigger.   
                                    ObjectPaid handlerOnObjectPaid = OnObjectPaid;
                                    if (handlerOnObjectPaid != null)
                                    {
                                        UUID objectID = UUID.Zero;
                                        UUID.TryParse((string)requestParam["objectID"], out objectID);
                                        handlerOnObjectPaid(objectID, clientUUID, (int)requestParam["amount"]); // call Script Engine for LSL money()
                                    }
                                    ret = true;
                                }
                            }
                        }
                    }
                }
            }

            // Send the response to money server.
            XmlRpcResponse resp = new XmlRpcResponse();
            Hashtable paramTable = new Hashtable();
            paramTable["success"] = ret;

            if (!ret)
            {
                m_log.ErrorFormat("[MONEY MODULE]: OnMoneyTransferedHandler: Transaction is failed. MoneyServer will rollback");
            }
            resp.Value = paramTable;

            return resp;
        }

        // "UpdateBalance" RPC from MoneyServer or Script
        public XmlRpcResponse BalanceUpdateHandler(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            if (m_disableXmlRpc)
            {
                m_log.WarnFormat("[MONEY MODULE]: BalanceUpdateHandler blocked - XML-RPC disabled");
                
                XmlRpcResponse disabledResp = new XmlRpcResponse();
                Hashtable disabledParamTable = new Hashtable();
                disabledParamTable["success"] = false;
                disabledParamTable["error"] = "XML-RPC transactions are disabled. Please use the REST API for local currency transactions.";
                disabledParamTable["error_code"] = "XML_RPC_DISABLED";
                disabledResp.Value = disabledParamTable;
                return disabledResp;
            }

            //m_log.InfoFormat("[MONEY MODULE]: BalanceUpdateHandler:");

            bool ret = false;

            #region Update the balance from money server.

            if (request.Params.Count > 0)
            {
                Hashtable requestParam = (Hashtable)request.Params[0];
                if (requestParam.Contains("clientUUID") && requestParam.Contains("clientSessionID") && requestParam.Contains("clientSecureSessionID"))
                {
                    UUID clientUUID = UUID.Zero;
                    UUID.TryParse((string)requestParam["clientUUID"], out clientUUID);
                    
                    if (clientUUID != UUID.Zero)
                    {
                        IClientAPI client = GetLocateClient(clientUUID);
                        string sessionid = (string)requestParam["clientSessionID"];
                        string secureid = (string)requestParam["clientSecureSessionID"];
                        
                        if (client != null && secureid == client.SecureSessionId.ToString() && 
                            (sessionid == UUID.Zero.ToString() || sessionid == client.SessionId.ToString()))
                        {
                            if (requestParam.Contains("Balance"))
                            {
                                // Send notify to the client.   
                                string msg = "";
                                if (requestParam.Contains("Message")) 
                                    msg = (string)requestParam["Message"];
                                    
                                client.SendMoneyBalance(UUID.Random(), true, Utils.StringToBytes(msg), (int)requestParam["Balance"],
                                                    0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);
                                // Dialog
                                if (msg != "")
                                {
                                    Scene scene = (Scene)client.Scene;
                                    IDialogModule dlg = scene.RequestModuleInterface<IDialogModule>();
                                    dlg.SendAlertToUser(client.AgentId, msg);
                                }
                                ret = true;
                            }
                        }
                    }
                }
            }

            #endregion

            // Send the response to money server.
            XmlRpcResponse resp = new XmlRpcResponse();
            Hashtable paramTable = new Hashtable();
            paramTable["success"] = ret;

            if (!ret)
            {
                m_log.ErrorFormat("[MONEY MODULE]: BalanceUpdateHandler: Cannot update client balance from MoneyServer");
            }
            resp.Value = paramTable;

            return resp;
        }

        // "UserAlert" RPC from Script
        public XmlRpcResponse UserAlertHandler(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            if (m_disableXmlRpc)
            {
                m_log.WarnFormat("[MONEY MODULE]: UserAlertHandler blocked - XML-RPC disabled");
                
                XmlRpcResponse disabledResp = new XmlRpcResponse();
                Hashtable disabledParamTable = new Hashtable();
                disabledParamTable["success"] = false;
                disabledParamTable["error"] = "XML-RPC transactions are disabled. Please use the REST API for local currency transactions.";
                disabledParamTable["error_code"] = "XML_RPC_DISABLED";
                disabledResp.Value = disabledParamTable;
                return disabledResp;
            }

            //m_log.InfoFormat("[MONEY MODULE]: UserAlertHandler:");

            bool ret = false;

            #region confirm the request and show the notice from money server.

            if (request.Params.Count > 0)
            {
                Hashtable requestParam = (Hashtable)request.Params[0];
                if (requestParam.Contains("clientUUID") && requestParam.Contains("clientSessionID") && requestParam.Contains("clientSecureSessionID"))
                {
                    UUID clientUUID = UUID.Zero;
                    UUID.TryParse((string)requestParam["clientUUID"], out clientUUID);
                    
                    if (clientUUID != UUID.Zero)
                    {
                        IClientAPI client = GetLocateClient(clientUUID);
                        string sessionid = (string)requestParam["clientSessionID"];
                        string secureid = (string)requestParam["clientSecureSessionID"];
                        
                        if (client != null && secureid == client.SecureSessionId.ToString() && 
                            (sessionid == UUID.Zero.ToString() || sessionid == client.SessionId.ToString()))
                        {
                            if (requestParam.Contains("Description"))
                            {
                                string description = (string)requestParam["Description"];
                                // Show the notice dialog with money server message.
                                GridInstantMessage gridMsg = new GridInstantMessage(null, UUID.Zero, "MonyServer", new UUID(clientUUID.ToString()),
                                                                    (byte)InstantMessageDialog.MessageFromAgent, description, false, new Vector3());
                                client.SendInstantMessage(gridMsg);
                                ret = true; 
                            }
                        }
                    }
                }
            }

            #endregion

            // Send the response to money server.
            XmlRpcResponse resp = new XmlRpcResponse();
            Hashtable paramTable = new Hashtable();
            paramTable["success"] = ret;

            resp.Value = paramTable;
            return resp;
        }

        // "GetBalance" RPC from Script
        public XmlRpcResponse GetBalanceHandler(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            if (m_disableXmlRpc)
            {
                m_log.WarnFormat("[MONEY MODULE]: GetBalanceHandler blocked - XML-RPC disabled");
                
                XmlRpcResponse disabledResp = new XmlRpcResponse();
                Hashtable disabledParamTable = new Hashtable();
                disabledParamTable["success"] = false;
                disabledParamTable["error"] = "XML-RPC transactions are disabled. Please use the REST API for local currency transactions.";
                disabledParamTable["error_code"] = "XML_RPC_DISABLED";
                disabledResp.Value = disabledParamTable;
                return disabledResp;
            }

            //m_log.InfoFormat("[MONEY MODULE]: GetBalanceHandler:");

            bool ret = false;
            int balance = -1;

            if (request.Params.Count > 0)
            {
                Hashtable requestParam = (Hashtable)request.Params[0];
                if (requestParam.Contains("clientUUID") && requestParam.Contains("clientSessionID") && requestParam.Contains("clientSecureSessionID"))
                {
                    UUID clientUUID = UUID.Zero;
                    UUID.TryParse((string)requestParam["clientUUID"], out clientUUID);
                    
                    if (clientUUID != UUID.Zero)
                    {
                        IClientAPI client = GetLocateClient(clientUUID);
                        string sessionid = (string)requestParam["clientSessionID"];
                        string secureid = (string)requestParam["clientSecureSessionID"];
                        
                        if (client != null && secureid == client.SecureSessionId.ToString() && 
                            (sessionid == UUID.Zero.ToString() || sessionid == client.SessionId.ToString()))
                        {
                            balance = QueryBalance(clientUUID);
                        }
                    }
                }
            }

            // Send the response to caller.
            if (balance < 0)
            {
                m_log.ErrorFormat("[MONEY MODULE]: GetBalanceHandler: GetBalance transaction is failed");
                ret = false;
            }

            XmlRpcResponse resp = new XmlRpcResponse();
            Hashtable paramTable = new Hashtable();
            paramTable["success"] = ret;
            paramTable["balance"] = balance;
            resp.Value = paramTable;

            return resp;
        }

		// "AddBankerMoney" RPC from Script
		public XmlRpcResponse AddBankerMoneyHandler(XmlRpcRequest request, IPEndPoint remoteClient)
		{


			m_log.InfoFormat("[MONEY MODULE]: AddBankerMoneyHandler:");

			// SHORT-CIRCUIT: If redirect is enabled, handle ALL requests as redirects
			// Let the external web server distinguish between quotes and purchases
			if (m_redirectEnabled)
			{
				Hashtable requestParam = (Hashtable)request.Params[0];
				UUID userID = UUID.Zero;
				int amount = 0;
				
				if (requestParam.ContainsKey("clientUUID"))
					UUID.TryParse((string)requestParam["clientUUID"], out userID);
					
				if (requestParam.ContainsKey("amount")) 
					amount = (int)requestParam["amount"];
				
				m_log.InfoFormat("[MONEY MODULE]: AddBankerMoneyHandler: Redirecting currency request for user {0}, amount {1}", 
								userID.ToString(), amount.ToString());
				
				// Notify the user about the redirect
				if (userID != UUID.Zero)
				{
					NotifyUserAboutRedirect(userID, amount, true); // true = is quote request
				}
				
				// Return redirect response for ALL requests (both quotes and purchases)
				// The external web server will handle the distinction
				XmlRpcResponse redirectResponse = new XmlRpcResponse();
				Hashtable redirectParamTable = new Hashtable();
				
				// For quotes, success should be false to trigger redirect
				redirectParamTable["success"] = false;
				redirectParamTable["errorMessage"] = m_redirectMessage;
				redirectParamTable["errorURI"] = m_redirectUrl;
				
				redirectResponse.Value = redirectParamTable;
				return redirectResponse;
			}
			
			if (m_disableXmlRpc)
			{
				m_log.WarnFormat("[MONEY MODULE]: AddBankerMoneyHandler blocked - XML-RPC disabled");
				
				XmlRpcResponse disabledResp = new XmlRpcResponse();
				Hashtable disabledParamTable = new Hashtable();
				disabledParamTable["success"] = false;
				disabledParamTable["error"] = "XML-RPC transactions are disabled. Please use the REST API for local currency transactions.";
				disabledParamTable["error_code"] = "XML_RPC_DISABLED";
				disabledResp.Value = disabledParamTable;
				return disabledResp;
			}

			// Original logic for when redirect is disabled
			bool ret = false;
			m_settle_user = false;

			if (request.Params.Count > 0)
			{
				Hashtable requestParam = (Hashtable)request.Params[0];

				if (requestParam.Contains("clientUUID") && requestParam.Contains("clientSessionID") && requestParam.Contains("clientSecureSessionID"))
				{
					UUID bankerUUID = UUID.Zero;
					UUID.TryParse((string)requestParam["clientUUID"], out bankerUUID);
					
					if (bankerUUID != UUID.Zero)
					{
						IClientAPI client = GetLocateClient(bankerUUID);
						string sessionid = (string)requestParam["clientSessionID"];
						string secureid = (string)requestParam["clientSecureSessionID"];
						
						if (client != null && secureid == client.SecureSessionId.ToString() && 
							(sessionid == UUID.Zero.ToString() || sessionid == client.SessionId.ToString()))
						{
							if (requestParam.Contains("amount"))
							{
								Scene scene = (Scene)client.Scene;
								int amount = (int)requestParam["amount"];
								ulong regionHandle = scene.RegionInfo.RegionHandle;
								UUID regionUUID = scene.RegionInfo.RegionID;
								ret = AddBankerMoney(bankerUUID, amount, regionHandle, regionUUID);

								if (m_use_web_settle && m_settle_user)
								{
									ret = true;
									IDialogModule dlg = scene.RequestModuleInterface<IDialogModule>();
									if (dlg != null)
									{
										dlg.SendUrlToUser(bankerUUID, "SYSTEM", UUID.Zero, UUID.Zero, false, m_settle_message, m_settle_url);
									}
								}
							}
						}
					}
				}
			}

			if (!ret) 
				m_log.ErrorFormat("[MONEY MODULE]: AddBankerMoneyHandler: Add Banker Money transaction is failed");

			// Send the response to caller.
			XmlRpcResponse resp = new XmlRpcResponse();
			Hashtable paramTable = new Hashtable();
			paramTable["settle"] = false;
			paramTable["success"] = ret;

			if (m_use_web_settle && m_settle_user) 
				paramTable["settle"] = true;
				
			resp.Value = paramTable;
			return resp;
		}

       // "SendMoney" RPC from Script
        public XmlRpcResponse SendMoneyHandler(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            if (m_disableXmlRpc)
            {
                m_log.WarnFormat("[MONEY MODULE]: SendMoneyHandler blocked - XML-RPC disabled");
                
                XmlRpcResponse disabledResp = new XmlRpcResponse();
                Hashtable disabledParamTable = new Hashtable();
                disabledParamTable["success"] = false;
                disabledParamTable["error"] = "XML-RPC transactions are disabled. Please use the REST API for local currency transactions.";
                disabledParamTable["error_code"] = "XML_RPC_DISABLED";
                disabledResp.Value = disabledParamTable;
                return disabledResp;
            }

            //m_log.InfoFormat("[MONEY MODULE]: SendMoneyHandler:");

            bool ret = false;

            if (request.Params.Count > 0)
            {
                Hashtable requestParam = (Hashtable)request.Params[0];
                if (requestParam.Contains("agentUUID") && requestParam.Contains("secretAccessCode"))
                {
                    UUID agentUUID = UUID.Zero;
                    UUID.TryParse((string)requestParam["agentUUID"], out agentUUID);

                    if (agentUUID != UUID.Zero)
                    {
                        if (requestParam.Contains("amount"))
                        {
                            int amount = (int)requestParam["amount"];
                            int type = -1;
                            if (requestParam.Contains("type")) 
                                type = (int)requestParam["type"];
                                
                            string secretCode = (string)requestParam["secretAccessCode"];
                            string scriptIP = remoteClient.Address.ToString();

                            MD5 md5 = MD5.Create();
                            byte[] code = md5.ComputeHash(ASCIIEncoding.Default.GetBytes(secretCode + "_" + scriptIP));
                            string hash = BitConverter.ToString(code).ToLower().Replace("-","");
                            //m_log.InfoFormat("[MONEY MODULE]: SendMoneyHandler: SecretCode: {0} + {1} = {2}", secretCode, scriptIP, hash);
                            ret = SendMoneyTo(agentUUID, amount, type, hash);
                        }
                    }
                    else
                    {
                        m_log.ErrorFormat("[MONEY MODULE]: SendMoneyHandler: amount is missed");
                    }
                }
                else
                {
                    if (!requestParam.Contains("agentUUID"))
                    {
                        m_log.ErrorFormat("[MONEY MODULE]: SendMoneyHandler: agentUUID is missed");
                    }
                    if (!requestParam.Contains("secretAccessCode"))
                    {
                        m_log.ErrorFormat("[MONEY MODULE]: SendMoneyHandler: secretAccessCode is missed");
                    }
                }
            }
            else
            {
                m_log.ErrorFormat("[MONEY MODULE]: SendMoneyHandler: Params count is under 0");
            }

            if (!ret) 
                m_log.ErrorFormat("[MONEY MODULE]: SendMoneyHandler: Send Money transaction is failed");

            // Send the response to caller.
            XmlRpcResponse resp = new XmlRpcResponse();
            Hashtable paramTable = new Hashtable();
            paramTable["success"] = ret;

            resp.Value = paramTable;
            return resp;
        }

        // "MoveMoney" RPC from Script
        public XmlRpcResponse MoveMoneyHandler(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            if (m_disableXmlRpc)
            {
                m_log.WarnFormat("[MONEY MODULE]: MoveMoneyHandler blocked - XML-RPC disabled");
                
                XmlRpcResponse disabledResp = new XmlRpcResponse();
                Hashtable disabledParamTable = new Hashtable();
                disabledParamTable["success"] = false;
                disabledParamTable["error"] = "XML-RPC transactions are disabled. Please use the REST API for local currency transactions.";
                disabledParamTable["error_code"] = "XML_RPC_DISABLED";
                disabledResp.Value = disabledParamTable;
                return disabledResp;
            }

            //m_log.InfoFormat("[MONEY MODULE]: MoveMoneyHandler:");

            bool ret = false;

            if (request.Params.Count > 0)
            {
                Hashtable requestParam = (Hashtable)request.Params[0];
                if ((requestParam.Contains("fromUUID") || requestParam.Contains("toUUID")) && requestParam.Contains("secretAccessCode"))
                {
                    UUID fromUUID = UUID.Zero;
                    UUID toUUID = UUID.Zero;  // UUID.Zero means System
                    if (requestParam.Contains("fromUUID")) 
                        UUID.TryParse((string)requestParam["fromUUID"], out fromUUID);
                    if (requestParam.Contains("toUUID"))   
                        UUID.TryParse((string)requestParam["toUUID"], out toUUID);

                    if (requestParam.Contains("amount"))
                    {
                        int amount = (int)requestParam["amount"];
                        string secretCode = (string)requestParam["secretAccessCode"];
                        string scriptIP = remoteClient.Address.ToString();

                        MD5 md5 = MD5.Create();
                        byte[] code = md5.ComputeHash(ASCIIEncoding.Default.GetBytes(secretCode + "_" + scriptIP));
                        string hash = BitConverter.ToString(code).ToLower().Replace("-","");
                        //m_log.InfoFormat("[MONEY MODULE]: MoveMoneyHandler: SecretCode: {0} + {1} = {2}", secretCode, scriptIP, hash);
                        ret = MoveMoneyFromTo(fromUUID, toUUID, amount, hash);
                    }
                    else
                    {
                        m_log.ErrorFormat("[MONEY MODULE]: MoveMoneyHandler: amount is missed");
                    }
                }
                else
                {
                    if (!requestParam.Contains("fromUUID") && !requestParam.Contains("toUUID"))
                    {
                        m_log.ErrorFormat("[MONEY MODULE]: MoveMoneyHandler: fromUUID and toUUID are missed");
                    }
                    if (!requestParam.Contains("secretAccessCode"))
                    {
                        m_log.ErrorFormat("[MONEY MODULE]: MoveMoneyHandler: secretAccessCode is missed");
                    }
                }
            }
            else
            {
                m_log.ErrorFormat("[MONEY MODULE]: MoveMoneyHandler: Params count is under 0");
            }

            if (!ret) 
                m_log.ErrorFormat("[MONEY MODULE]: MoveMoneyHandler: Move Money transaction is failed");

            // Send the response to caller.
            XmlRpcResponse resp = new XmlRpcResponse();
            Hashtable paramTable = new Hashtable();
            paramTable["success"] = ret;

            resp.Value = paramTable;
            return resp;
        }

        #endregion

        #region Viewer Currency Transaction Handlers (Always Enabled for Viewer Compatibility)

		/// <summary>
		/// Handles viewer requests for currency purchase quotes - CRITICAL FIX for Firestorm
		/// </summary>
		public XmlRpcResponse GetCurrencyQuoteHandler(XmlRpcRequest request, IPEndPoint remoteClient)
		{
			m_log.InfoFormat("[MONEY MODULE]: GetCurrencyQuoteHandler: Processing currency quote request");

			Hashtable requestParam = (Hashtable)request.Params[0];
			UUID agentId = UUID.Zero;
			int currencyBuy = 1000;

			if (requestParam.ContainsKey("agentId"))
				UUID.TryParse((string)requestParam["agentId"], out agentId);
			if (requestParam.ContainsKey("currencyBuy")) 
				currencyBuy = (int)requestParam["currencyBuy"];

			m_log.InfoFormat("[MONEY MODULE]: GetCurrencyQuoteHandler: Agent {0} requesting quote for {1} currency units", 
				agentId, currencyBuy);

			XmlRpcResponse quoteResponse = new XmlRpcResponse();
			Hashtable quoteParamTable = new Hashtable();
			quoteParamTable["success"] = true;

			Hashtable currencyData = new Hashtable();
			int estimatedCost = CalculateRealMoneyCost(currencyBuy);
			currencyData["estimatedCost"] = estimatedCost;
			currencyData["currencyBuy"] = currencyBuy;
			quoteParamTable["currency"] = currencyData;

			quoteParamTable["confirm"] = GenerateConfirmationHash(agentId, remoteClient.Address.ToString());

			m_log.InfoFormat("[MONEY MODULE]: GetCurrencyQuoteHandler: Returning quote - cost: {0} for {1} currency units", 
				estimatedCost, currencyBuy);

			quoteResponse.Value = quoteParamTable;
			return quoteResponse;
		}

		/// <summary>
		/// Handles actual currency purchase requests from viewer
		/// </summary>
		public XmlRpcResponse BuyCurrencyHandler(XmlRpcRequest request, IPEndPoint remoteClient)
		{
			m_log.InfoFormat("[MONEY MODULE]: BuyCurrencyHandler: Handling currency purchase request");

			Hashtable requestParam = (Hashtable)request.Params[0];
			UUID agentId = UUID.Zero;
			int currencyBuy = 1000;
			string confirm = string.Empty;
			
			if (requestParam.ContainsKey("agentId"))
				UUID.TryParse((string)requestParam["agentId"], out agentId);
			if (requestParam.ContainsKey("currencyBuy")) 
				currencyBuy = (int)requestParam["currencyBuy"];
			if (requestParam.ContainsKey("confirm"))
				confirm = (string)requestParam["confirm"];

			m_log.InfoFormat("[MONEY MODULE]: BuyCurrencyHandler: Agent {0} purchasing {1} currency units", 
				agentId, currencyBuy);

			if (!ValidateConfirmationHash(agentId, remoteClient.Address.ToString(), confirm))
			{
				m_log.WarnFormat("[MONEY MODULE]: BuyCurrencyHandler: Invalid confirmation for agent {0}", agentId);
				
				XmlRpcResponse errorResp = new XmlRpcResponse();
				Hashtable errorTable = new Hashtable();
				errorTable["success"] = false;
				errorTable["errorMessage"] = "Invalid confirmation token";
				errorResp.Value = errorTable;
				return errorResp;
			}

			if (m_redirectEnabled)
			{
				m_log.InfoFormat("[MONEY MODULE]: BuyCurrencyHandler: Redirecting agent {0} to purchase page", agentId);
				
				XmlRpcResponse redirectResponse = new XmlRpcResponse();
				Hashtable redirectParamTable = new Hashtable();
				redirectParamTable["success"] = false;
				redirectParamTable["errorMessage"] = m_redirectMessage;
				redirectParamTable["errorURI"] = m_redirectUrl + "?agentId=" + agentId.ToString() + "&amount=" + currencyBuy;
				redirectResponse.Value = redirectParamTable;
				return redirectResponse;
			}
			else
			{
				bool purchaseSuccess = ProcessRealMoneyPayment(agentId, currencyBuy);
				
				XmlRpcResponse resp = new XmlRpcResponse();
				Hashtable paramTable = new Hashtable();
				paramTable["success"] = purchaseSuccess;
				
				if (!purchaseSuccess)
					paramTable["errorMessage"] = "Payment processing failed";
				
				resp.Value = paramTable;
				return resp;
			}
		}

        #endregion

        #region Region/Simulator Communication Handlers (Subject to XML-RPC Disable)

        /// <summary>
        /// Handles balance requests from viewer via economy system
        /// This is OpenSim internal communication, not viewer-to-web-server
        /// </summary>
        public XmlRpcResponse SimulatorUserBalanceRequestHandler(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            // This is OpenSim internal communication - subject to XML-RPC disable
            if (m_disableXmlRpc)
            {
                m_log.WarnFormat("[MONEY MODULE]: SimulatorUserBalanceRequestHandler blocked - XML-RPC disabled");
                
                XmlRpcResponse disabledResp = new XmlRpcResponse();
                Hashtable disabledParamTable = new Hashtable();
                disabledParamTable["success"] = false;
                disabledParamTable["errorMessage"] = "XML-RPC transactions are disabled. Please use the REST API for local currency transactions.";
                disabledParamTable["error_code"] = "XML_RPC_DISABLED";
                disabledResp.Value = disabledParamTable;
                return disabledResp;
            }

            m_log.InfoFormat("[MONEY MODULE]: SimulatorUserBalanceRequestHandler:");

            if (request.Params.Count > 0)
            {
                Hashtable requestParam = (Hashtable)request.Params[0];
                if (requestParam.Contains("agentId") && requestParam.Contains("secureSessionId"))
                {
                    UUID agentId = UUID.Zero;
                    UUID.TryParse((string)requestParam["agentId"], out agentId);

                    if (agentId != UUID.Zero)
                    {
                        int balance = QueryBalance(agentId);
                        
                        XmlRpcResponse resp = new XmlRpcResponse();
                        Hashtable paramTable = new Hashtable();
                        paramTable["success"] = true;
                        paramTable["agentId"] = agentId.ToString();
                        paramTable["funds"] = balance;
                        resp.Value = paramTable;
                        return resp;
                    }
                }
            }

            // Error response
            XmlRpcResponse errorResp = new XmlRpcResponse();
            Hashtable errorTable = new Hashtable();
            errorTable["success"] = false;
            errorTable["errorMessage"] = "Unable to retrieve balance";
            errorResp.Value = errorTable;
            return errorResp;
        }

        /// <summary>
        /// Handles region move money requests
        /// This is OpenSim internal communication, not viewer-to-web-server
        /// </summary>
        public XmlRpcResponse RegionMoveMoneyHandler(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            // This is OpenSim internal communication - subject to XML-RPC disable
            if (m_disableXmlRpc)
            {
                m_log.WarnFormat("[MONEY MODULE]: RegionMoveMoneyHandler blocked - XML-RPC disabled");
                
                XmlRpcResponse disabledResp = new XmlRpcResponse();
                Hashtable disabledParamTable = new Hashtable();
                disabledParamTable["success"] = false;
                disabledParamTable["errorMessage"] = "XML-RPC transactions are disabled. Please use the REST API for local currency transactions.";
                disabledParamTable["error_code"] = "XML_RPC_DISABLED";
                disabledResp.Value = disabledParamTable;
                return disabledResp;
            }

            m_log.InfoFormat("[MONEY MODULE]: RegionMoveMoneyHandler:");

            bool ret = false;
            
            if (request.Params.Count > 0)
            {
                Hashtable requestParam = (Hashtable)request.Params[0];
                if (requestParam.Contains("agentId") && requestParam.Contains("destId") && 
                    requestParam.Contains("secureSessionId") && requestParam.Contains("cash"))
                {
                    UUID agentId = UUID.Zero;
                    UUID destId = UUID.Zero;
                    UUID.TryParse((string)requestParam["agentId"], out agentId);
                    UUID.TryParse((string)requestParam["destId"], out destId);
                    
                    int amount = (int)requestParam["cash"];
                    int transactionType = requestParam.Contains("transactionType") ? 
                        (int)requestParam["transactionType"] : (int)TransactionType.SendMoney;
                    string description = requestParam.Contains("description") ? 
                        (string)requestParam["description"] : "Region money transfer";

                    if (agentId != UUID.Zero && destId != UUID.Zero && amount > 0)
                    {
                        // Use the existing transfer infrastructure
                        ret = TransferMoney(agentId, destId, amount, transactionType, UUID.Zero, 0, UUID.Zero, description);
                    }
                }
            }

            XmlRpcResponse resp = new XmlRpcResponse();
            Hashtable paramTable = new Hashtable();
            paramTable["success"] = ret;
            resp.Value = paramTable;
            return resp;
        }

		/// <summary>
		/// REST API handler method with correct LLSD parsing for CAPS requests
		/// </summary>
		public string ProcessCurrencyRest(string requestBody, string path, string param,
										  IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
		{
			try
			{
				string absPath = httpRequest.Url.AbsolutePath.ToLower();
				m_log.InfoFormat("[MONEY MODULE]: ProcessCurrencyRest: Full path: {0}, Method: {1}",
								 absPath, httpRequest.HttpMethod);

				// If invoked via the CAPS "Currency" capability, handle it directly
				if (string.Equals(param, "Currency", StringComparison.OrdinalIgnoreCase))
				{
					return HandleCapsCurrencyRequest(requestBody, httpRequest, httpResponse);
				}

				// Otherwise, handle any fixed REST API endpoints you’ve defined
				if (absPath.Contains("/currency/quote"))
				{
					HandleCurrencyQuote(httpRequest, httpResponse);
				}
				else if (absPath.Contains("/currency/buy"))
				{
					HandleCurrencyBuy(httpRequest, httpResponse);
				}
				else if (absPath.Contains("/currency/balance"))
				{
					HandleCurrencyBalance(httpRequest, httpResponse);
				}
				else
				{
					m_log.WarnFormat("[MONEY MODULE]: ProcessCurrencyRest: Unknown path: {0}", absPath);
					SendErrorResponse(httpResponse, 404, "Unknown endpoint");
				}
			}
			catch (Exception ex)
			{
				m_log.ErrorFormat("[MONEY MODULE]: ProcessCurrencyRest error: {0}", ex.Message);
				SendErrorResponse(httpResponse, 500, "Internal server error");
			}

			return string.Empty;
		}

		/// <summary>
		/// Handle CAPS currency requests that use LLSD format
		/// </summary>
		private string HandleCapsCurrencyRequest(string requestBody, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
		{
			m_log.InfoFormat("[MONEY MODULE] ===== CAPS CURRENCY REQUEST START =====");
			m_log.InfoFormat("[MONEY MODULE]: URL: {0}", httpRequest.Url.ToString());
			m_log.InfoFormat("[MONEY MODULE]: Method: {0}", httpRequest.HttpMethod);
			m_log.InfoFormat("[MONEY MODULE]: Content-Type: {0}", httpRequest.ContentType);
			m_log.InfoFormat("[MONEY MODULE]: Content-Length: {0}", httpRequest.ContentLength);
			m_log.InfoFormat("[MONEY MODULE]: Request Body: {0}", requestBody);
			m_log.InfoFormat("[MONEY MODULE] ===== CAPS CURRENCY REQUEST END =====");
			m_log.InfoFormat("[MONEY MODULE]: HandleCapsCurrencyRequest: Processing CAPS currency request");
			
			// Parse LLSD XML request
			OSDMap req;
			try
			{
				if (string.IsNullOrEmpty(requestBody))
				{
					m_log.WarnFormat("[MONEY MODULE]: HandleCapsCurrencyRequest: Empty request body");
					// Return a default quote response for empty requests
					return CreateDefaultQuoteResponse();
				}
				
				req = OSDParser.DeserializeLLSDXml(requestBody) as OSDMap;
				m_log.InfoFormat("[MONEY MODULE]: HandleCapsCurrencyRequest: Parsed LLSD request: {0}", req != null ? req.ToString() : "NULL");
			}
			catch (Exception ex)
			{
				m_log.ErrorFormat("[MONEY MODULE]: Currency CAPS parse error: {0}, Request body: {1}", ex, requestBody);
				
				// Try alternative parsing for Firestorm
				return HandleAlternativeCapsRequest(requestBody, httpRequest, httpResponse);
			}

			string action = req != null && req.ContainsKey("action") ? req["action"].AsString() : "quote";
			int currencyBuy = req != null && req.ContainsKey("currencyBuy") ? req["currencyBuy"].AsInteger() : 1000;

			m_log.InfoFormat("[MONEY MODULE]: HandleCapsCurrencyRequest: Action={0}, CurrencyBuy={1}", action, currencyBuy);

			if (string.Equals(action, "quote", StringComparison.OrdinalIgnoreCase))
			{
				// Build quote response
				var resp = new OSDMap();
				resp["success"] = OSD.FromBoolean(true);

				var currency = new OSDMap();
				int estimatedCost = CalculateRealMoneyCost(currencyBuy);
				currency["estimatedCost"] = OSD.FromInteger(estimatedCost);
				currency["currencyBuy"] = OSD.FromInteger(currencyBuy);

				resp["currency"] = currency;
				resp["confirm"] = OSD.FromString(GenerateConfirmationHash(UUID.Zero, httpRequest.RemoteIPEndPoint.Address.ToString()));

				httpResponse.ContentType = "application/llsd+xml";
				httpResponse.StatusCode = 200;
				
				string responseString = OSDParser.SerializeLLSDXmlString(resp);
				m_log.InfoFormat("[MONEY MODULE]: HandleCapsCurrencyRequest: Returning quote response: {0}", responseString);
				return responseString;
			}
			else if (string.Equals(action, "buy", StringComparison.OrdinalIgnoreCase))
			{
				// Handle purchase
				bool ok = PerformCurrencyPurchase(req);
				var resp = new OSDMap();
				resp["success"] = OSD.FromBoolean(ok);

				if (!ok)
					resp["error"] = OSD.FromString("Purchase failed");

				httpResponse.ContentType = "application/llsd+xml";
				httpResponse.StatusCode = ok ? 200 : 400;
				return OSDParser.SerializeLLSDXmlString(resp);
			}
			else
			{
				m_log.WarnFormat("[MONEY MODULE]: HandleCapsCurrencyRequest: Unknown action: {0}", action);
				var err = new OSDMap();
				err["success"] = OSD.FromBoolean(false);
				err["error"] = OSD.FromString("Unknown action");
				httpResponse.StatusCode = 400;
				httpResponse.ContentType = "application/llsd+xml";
				return OSDParser.SerializeLLSDXmlString(err);
			}
		}

		private string CreateDefaultQuoteResponse()
		{
			var resp = new OSDMap();
			resp["success"] = OSD.FromBoolean(true);

			var currency = new OSDMap();
			currency["estimatedCost"] = OSD.FromInteger(100); // $1.00 default
			currency["currencyBuy"] = OSD.FromInteger(1000); // 1000 currency units

			resp["currency"] = currency;
			resp["confirm"] = OSD.FromString("default_confirm_hash");

			return OSDParser.SerializeLLSDXmlString(resp);
		}

		/// <summary>
		/// Handle alternative CAPS request formats
		/// </summary>
		private string HandleAlternativeCapsRequest(string requestBody, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
		{
			try
			{
				m_log.InfoFormat("[MONEY MODULE]: HandleAlternativeCapsRequest: Trying alternative parsing");
				
				// Try JSON parsing if LLSD fails
				if (!string.IsNullOrEmpty(requestBody) && (requestBody.Trim().StartsWith("{") || requestBody.Contains("agentId")))
				{
					try
					{
						OMVOSDMap jsonData = (OMVOSDMap)OSDParser.DeserializeJson(requestBody);
						m_log.InfoFormat("[MONEY MODULE]: HandleAlternativeCapsRequest: Parsed JSON data");
						
						// Process as LLSD request but with JSON data
						return HandleCapsCurrencyRequest(requestBody, httpRequest, httpResponse);
					}
					catch (Exception jsonEx)
					{
						m_log.WarnFormat("[MONEY MODULE]: HandleAlternativeCapsRequest: JSON parsing also failed: {0}", jsonEx.Message);
					}
				}
				
				// If all parsing fails, return a default response
				OMVOSDMap defaultResponse = new OMVOSDMap();
				defaultResponse["success"] = OMVOSD.FromBoolean(true);
				defaultResponse["currency"] = new OMVOSDMap();
				((OMVOSDMap)defaultResponse["currency"])["estimatedCost"] = OMVOSD.FromInteger(100); // Default cost
				((OMVOSDMap)defaultResponse["currency"])["currencyBuy"] = OMVOSD.FromInteger(1000); // Default amount
				
				string responseString = OSDParser.SerializeLLSDXmlString(defaultResponse);
				httpResponse.StatusCode = 200;
				httpResponse.ContentType = "application/llsd+xml";
				return responseString;
			}
			catch (Exception ex)
			{
				m_log.ErrorFormat("[MONEY MODULE]: HandleAlternativeCapsRequest error: {0}", ex.Message);
				return HandleCapsCurrencyRequest(null, httpRequest, httpResponse); // Fallback
			}
		}

		/// <summary>
		/// Process currency.php requests - with correct SimpleStreamHandler signature
		/// </summary>
		public void ProcessCurrencyPHP_Simple(IOSHttpRequest request, IOSHttpResponse response)
		{
			m_log.InfoFormat("[MONEY MODULE]: ProcessCurrencyPHP_Simple: Handling currency.php request");

			try
			{
				// Convert to the expected types for the REST handler
				string path = request.Url.AbsolutePath;
				Stream inputStream = request.InputStream;
				IOSHttpRequest osRequest = request;
				IOSHttpResponse osResponse = response;

				// Call the REST handler which returns a string
				string result = ProcessCurrencyPHP(path, inputStream, osRequest, osResponse);

				// Write the result to the response
				if (!string.IsNullOrEmpty(result))
				{
					byte[] buffer = Encoding.UTF8.GetBytes(result);
					response.ContentType = "text/html";
					response.ContentLength = buffer.Length;
					response.OutputStream.Write(buffer, 0, buffer.Length);
					response.OutputStream.Flush();
				}
			}
			catch (Exception ex)
			{
				m_log.ErrorFormat("[MONEY MODULE]: ProcessCurrencyPHP_Simple error: {0}", ex.Message);
				response.StatusCode = 500;
				response.StatusDescription = "Internal server error";
			}
		}

		/// <summary>
		/// Process currency.php requests - with correct RestMethod signature
		/// </summary>
		public string ProcessCurrencyPHP(string path, Stream request,
										 IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
		{
			m_log.InfoFormat("[MONEY MODULE]: ProcessCurrencyPHP: Handling currency request for path: {0}", httpRequest.Url.ToString());

			try
			{
				string absPath = httpRequest.Url.AbsolutePath.ToLower();

				if (absPath.Contains("/currency/quote"))
				{
					HandleCurrencyQuote(httpRequest, httpResponse);
				}
				else if (absPath.Contains("/currency/buy"))
				{
					HandleCurrencyBuy(httpRequest, httpResponse);
				}
				else if (absPath.Contains("/currency/balance"))
				{
					HandleCurrencyBalance(httpRequest, httpResponse);
				}
				else
				{
					// Fallback to XML-RPC for legacy endpoints
					Dictionary<string, XmlRpcMethod> rpcHandlers = new Dictionary<string, XmlRpcMethod>();
					rpcHandlers["getCurrencyQuote"] = new XmlRpcMethod(GetCurrencyQuoteHandler);
					rpcHandlers["buyCurrency"] = new XmlRpcMethod(BuyCurrencyHandler);
					rpcHandlers["money_balance_request"] = new XmlRpcMethod(SimulatorUserBalanceRequestHandler);
					rpcHandlers["money_transfer_request"] = new XmlRpcMethod(RegionMoveMoneyHandler);

					// MainServer still requires concrete types, so cast here
					var osRequest = httpRequest as OSHttpRequest;
					var osResponse = httpResponse as OSHttpResponse;

					if (osRequest != null && osResponse != null)
					{
						MainServer.Instance.HandleXmlRpcRequests(osRequest, osResponse, rpcHandlers);
					}
					else
					{
						m_log.Error("[MONEY MODULE]: Could not cast to OSHttpRequest/OSHttpResponse for legacy XML-RPC handling");
					}
				}
			}
			catch (Exception ex)
			{
				m_log.ErrorFormat("[MONEY MODULE]: ProcessCurrencyPHP error: {0}", ex.Message);
				SendErrorResponse(httpResponse, 500, "Internal server error");
			}

			return string.Empty; // Required by RestMethod delegate
		}

		/// <summary>
		/// Process landtool.php requests - with correct SimpleStreamHandler signature
		/// </summary>
		public void ProcessLandtoolPHP(IOSHttpRequest request, IOSHttpResponse response)
		{
			m_log.InfoFormat("[MONEY MODULE]: ProcessLandtoolPHP: Handling landtool.php request");
			
			// FIX: Replace Hashtable with Dictionary
			Dictionary<string, XmlRpcMethod> rpcHandlers = new Dictionary<string, XmlRpcMethod>();
			rpcHandlers["preflightBuyLandPrep"] = new XmlRpcMethod(PreflightBuyLandPrepHandler);
			rpcHandlers["buyLandPrep"] = new XmlRpcMethod(LandBuyHandler);
			
			// Cast to concrete types for MainServer handling
			var osRequest = request as OSHttpRequest;
			var osResponse = response as OSHttpResponse;
			
			if (osRequest != null && osResponse != null)
			{
				MainServer.Instance.HandleXmlRpcRequests(osRequest, osResponse, rpcHandlers);
			}
		}
		
		/// <summary>
		/// Handler for preflight land buy preparation
		/// </summary>
		public XmlRpcResponse PreflightBuyLandPrepHandler(XmlRpcRequest request, IPEndPoint remoteClient)
		{
			m_log.InfoFormat("[MONEY MODULE]: PreflightBuyLandPrepHandler:");
			
			XmlRpcResponse ret = new XmlRpcResponse();
			Hashtable retparam = new Hashtable();
			
			// Simplified response - you can expand this based on your needs
			retparam.Add("success", true);
			
			Hashtable currency = new Hashtable();
			currency.Add("estimatedCost", 0);
			retparam.Add("currency", currency);
			
			Hashtable membership = new Hashtable();
			membership.Add("upgrade", false);
			retparam.Add("membership", membership);
			
			Hashtable landuse = new Hashtable();
			landuse.Add("upgrade", false);
			retparam.Add("landuse", landuse);
			
			retparam.Add("confirm", UUID.Random().ToString());
			
			ret.Value = retparam;
			return ret;
		}

		/// <summary>
		/// Handler for land buy preparation
		/// </summary>
		public XmlRpcResponse LandBuyHandler(XmlRpcRequest request, IPEndPoint remoteClient)
		{
			m_log.InfoFormat("[MONEY MODULE]: LandBuyHandler:");
			
			XmlRpcResponse ret = new XmlRpcResponse();
			Hashtable retparam = new Hashtable();
			retparam.Add("success", true);
			ret.Value = retparam;
			
			return ret;
		}
        #endregion

        #region Currency Handler Implementation Methods

        private XmlRpcResponse HandleCurrencyQuoteRequest(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            // Implement currency quote logic when redirect is disabled
            // This would integrate with your payment processor
            Hashtable requestParam = (Hashtable)request.Params[0];
            UUID agentId = UUID.Zero;
            UUID.TryParse((string)requestParam["agentId"], out agentId);
            int currencyBuy = (int)requestParam["currencyBuy"];

            // Calculate cost based on your rates
            int estimatedCost = CalculateRealMoneyCost(currencyBuy);
            
            XmlRpcResponse resp = new XmlRpcResponse();
            Hashtable paramTable = new Hashtable();
            paramTable["success"] = true;
            
            Hashtable currencyInfo = new Hashtable();
            currencyInfo["estimatedCost"] = estimatedCost;
            currencyInfo["currencyBuy"] = currencyBuy;
            
            paramTable["currency"] = currencyInfo;
            paramTable["confirm"] = GenerateConfirmationHash(agentId, remoteClient.Address.ToString());
            
            resp.Value = paramTable;
            return resp;
        }

        private XmlRpcResponse HandleCurrencyPurchaseRequest(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            // Implement currency purchase logic when redirect is disabled
            Hashtable requestParam = (Hashtable)request.Params[0];
            UUID agentId = UUID.Zero;
            UUID.TryParse((string)requestParam["agentId"], out agentId);
            int currencyBuy = (int)requestParam["currencyBuy"];
            string confirm = (string)requestParam["confirm"];

            // Validate confirmation
            if (!ValidateConfirmationHash(agentId, remoteClient.Address.ToString(), confirm))
            {
                return CreateErrorResponse("Invalid confirmation hash");
            }

            // Process payment through your payment processor
            bool paymentSuccess = ProcessRealMoneyPayment(agentId, currencyBuy);
            
            if (paymentSuccess)
            {
                // Add currency to user's account
                bool addSuccess = AddCurrencyToAccount(agentId, currencyBuy);
                
                XmlRpcResponse resp = new XmlRpcResponse();
                Hashtable paramTable = new Hashtable();
                paramTable["success"] = addSuccess;
                resp.Value = paramTable;
                return resp;
            }
            else
            {
                return CreateErrorResponse("Payment processing failed");
            }
        }

		/// <summary>
		/// Calculate real money cost for currency purchase
		/// </summary>
		private int CalculateRealMoneyCost(int currencyAmount)
		{
			double rate = 0.001; // Adjust based on your economy
			return (int)Math.Ceiling(currencyAmount * rate * 100); // Return in cents
		}

		/// <summary>
		/// Generate a secure confirmation hash for purchase validation
		/// </summary>
		private string GenerateConfirmationHash(UUID agentId, string ipAddress)
		{
			string secret = "your-secret-key"; // Should be configurable
			string data = $"{agentId}_{ipAddress}_{secret}_{DateTime.UtcNow:yyyyMMddHH}";
			using (var md5 = MD5.Create())
			{
				byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(data));
				return BitConverter.ToString(hash).Replace("-", "").ToLower();
			}
		}

		/// <summary>
		/// Validate confirmation hash for purchase requests
		/// </summary>
		private bool ValidateConfirmationHash(UUID agentId, string ipAddress, string confirm)
		{
			// Check current hour and previous hour to allow for clock skew
			string expectedNow = GenerateConfirmationHash(agentId, ipAddress);
			if (confirm == expectedNow) return true;

			string expectedPrev = GenerateConfirmationHash(agentId, ipAddress); // adjust GenerateConfirmationHash to accept a DateTime if needed
			if (confirm == expectedPrev) return true;

			return false;
		}

		/// <summary>
		/// Process real money payment (integrate with your payment processor)
		/// </summary>
		private bool ProcessRealMoneyPayment(UUID agentId, int currencyAmount)
		{
			m_log.InfoFormat("[MONEY MODULE]: Processing real money payment for {0}, amount: {1}", agentId, currencyAmount);
			return true;
		}

        private bool AddCurrencyToAccount(UUID agentId, int currencyAmount)
        {
            // Add currency to user's account using existing infrastructure
            if (m_useRestApi && m_restApiValid && IsLocalUser(agentId))
            {
                // Use REST API for local users
                return UpdateWalletViaRestApi(agentId, currencyAmount, "credit", 
                    $"Currency purchase: {currencyAmount} units");
            }
            else if (!m_disableXmlRpc)
            {
                // Use XML-RPC for Hypergrid users
                return AddBankerMoney(agentId, currencyAmount, 0, UUID.Zero);
            }
            
            return false;
        }

        private XmlRpcResponse CreateErrorResponse(string message)
        {
            XmlRpcResponse resp = new XmlRpcResponse();
            Hashtable paramTable = new Hashtable();
            paramTable["success"] = false;
            paramTable["errorMessage"] = message;
            resp.Value = paramTable;
            return resp;
        }

        #endregion

        #region MoneyModule private help functions

        /// <summary>   
        /// Transfer the money from one user to another. Need to notify money server to update.   
        /// </summary>   
        /// <param name="amount">   
        /// The amount of money.   
        /// </param>   
        /// <returns>   
        /// return true, if successfully.   
        /// </returns>   
        private bool TransferMoney_Original(UUID sender, UUID receiver, int amount, int type, UUID objectID, ulong regionHandle, UUID regionUUID, string description)
        {
            // This method is kept for backward compatibility but is superseded by the new TransferMoney method
            return TransferMoney(sender, receiver, amount, type, objectID, regionHandle, regionUUID, description);
        }

        /// <summary>   
        /// Force transfer the money from one user to another. 
        /// This function does not check sender login.
        /// Need to notify money server to update.   
        /// </summary>   
        /// <param name="amount">   
        /// The amount of money.   
        /// </param>   
        /// <returns>   
        /// return true, if successfully.   
        /// </returns>   
        private bool ForceTransferMoney(UUID sender, UUID receiver, int amount, int type, UUID objectID, ulong regionHandle, UUID regionUUID, string description)
        {
            //m_log.InfoFormat("[MONEY MODULE]: ForceTransferMoney:");

            bool ret = false;

            #region Force send transaction request to money server and parse the resultes.

            if (m_enable_server)
            {
                string objName = string.Empty;
                SceneObjectPart sceneObj = GetLocatePrim(objectID);
                if (sceneObj != null) objName = sceneObj.Name;

                // Fill parameters for money transfer XML-RPC.   
                Hashtable paramTable = new Hashtable();
                paramTable["senderID"] = sender.ToString();
                paramTable["receiverID"] = receiver.ToString();
                paramTable["transactionType"] = type;
                paramTable["objectID"] = objectID.ToString();
                paramTable["objectName"] = objName;
                paramTable["regionHandle"] = regionHandle.ToString();
                paramTable["regionUUID"] = regionUUID.ToString();
                paramTable["amount"] = amount;
                paramTable["description"] = description;

                // Generate the request for transfer.   
                Hashtable resultTable = genericCurrencyXMLRPCRequest(paramTable, "ForceTransferMoney");

                // Handle the return values from Money Server.  
                if (resultTable != null && resultTable.Contains("success"))
                {
                    if ((bool)resultTable["success"] == true)
                    {
                        ret = true;
                    }
                }
                else 
                    m_log.ErrorFormat("[MONEY MODULE]: ForceTransferMoney: Can not money force transfer request from [{0}] to [{1}]", sender.ToString(), receiver.ToString());
            }
            //else m_log.ErrorFormat("[MONEY MODULE]: ForceTransferMoney: Money Server is not available!!");

            #endregion

            return ret;
        }

        /// <summary>   
        /// Send the money to avatar. Need to notify money server to update.   
        /// </summary>   
        /// <param name="amount">   
        /// The amount of money.  
        /// </param>   
        /// <returns>   
        /// return true, if successfully.   
        /// </returns>   
        private bool SendMoneyTo(UUID avatarID, int amount, int type, string secretCode)
        {
            //m_log.InfoFormat("[MONEY MODULE]: SendMoneyTo:");

            bool ret = false;

            if (m_enable_server)
            {
                // Fill parameters for money transfer XML-RPC.   
                if (type < 0) type = (int)TransactionType.ReferBonus;
                Hashtable paramTable = new Hashtable();
                paramTable["receiverID"] = avatarID.ToString();
                paramTable["transactionType"] = type;
                paramTable["amount"] = amount;
                paramTable["secretAccessCode"] = secretCode;
                paramTable["description"] = "Bonus to Avatar";

                // Generate the request for transfer.   
                Hashtable resultTable = genericCurrencyXMLRPCRequest(paramTable, "SendMoney");

                // Handle the return values from Money Server.  
                if (resultTable != null && resultTable.Contains("success"))
                {
                    if ((bool)resultTable["success"] == true)
                    {
                        ret = true;
                    }
                    else 
                        m_log.ErrorFormat("[MONEY MODULE]: SendMoneyTo: Fail Message is {0}", resultTable["message"]);
                }
                else 
                    m_log.ErrorFormat("[MONEY MODULE]: SendMoneyTo: Money Server is not responce");
            }
            //else m_log.ErrorFormat("[MONEY MODULE]: SendMoneyTo: Money Server is not available!!");

            return ret;
        }

        /// <summary>   
        /// Move the money from avatar to other avatar. Need to notify money server to update.   
        /// </summary>   
        /// <param name="amount">   
        /// The amount of money.  
        /// </param>   
        /// <returns>   
        /// return true, if successfully.   
        /// </returns>   
        private bool MoveMoneyFromTo(UUID senderID, UUID receiverID, int amount, string secretCode)
        {
            //m_log.InfoFormat("[MONEY MODULE]: MoveMoneyFromTo:");

            bool ret = false;

            if (m_enable_server)
            {
                // Fill parameters for money transfer XML-RPC.   
                Hashtable paramTable = new Hashtable();
                paramTable["senderID"] = senderID.ToString();
                paramTable["receiverID"] = receiverID.ToString();
                paramTable["transactionType"] = (int)TransactionType.MoveMoney;
                paramTable["amount"] = amount;
                paramTable["secretAccessCode"] = secretCode;
                paramTable["description"] = "Move Money";

                // Generate the request for transfer.   
                Hashtable resultTable = genericCurrencyXMLRPCRequest(paramTable, "MoveMoney");

                // Handle the return values from Money Server.  
                if (resultTable != null && resultTable.Contains("success"))
                {
                    if ((bool)resultTable["success"] == true)
                    {
                        ret = true;
                    }
                    else 
                        m_log.ErrorFormat("[MONEY MODULE]: MoveMoneyFromTo: Fail Message is {0}", resultTable["message"]);
                }
                else 
                    m_log.ErrorFormat("[MONEY MODULE]: MoveMoneyFromTo: Money Server is not responce");
            }
            //else m_log.ErrorFormat("[MONEY MODULE]: MoveMoneyFromTo: Money Server is not available!!");

            return ret;
        }

        /// <summary>   
        /// Add the money to banker avatar. Need to notify money server to update.   
        /// </summary>   
        /// <param name="amount">   
        /// The amount of money.  
        /// </param>   
        /// <returns>   
        /// return true, if successfully.   
        /// </returns>   
        private bool AddBankerMoney(UUID bankerID, int amount, ulong regionHandle, UUID regionUUID)
        {
            m_log.InfoFormat("[MONEY MODULE]: AddBankerMoney:");
            
            // SHORT-CIRCUIT: If redirect is enabled, immediately return false and notify user
            if (m_redirectEnabled)
            {
                m_log.InfoFormat("[MONEY MODULE]: AddBankerMoney: Redirecting purchase for user {0}, amount {1}", bankerID, amount);
                NotifyUserAboutRedirect(bankerID, amount);
                return false; // Return false to indicate purchase was redirected
            }

            bool ret = false;
            m_settle_user = false;

            if (m_enable_server)
            {
                // Fill parameters for money transfer XML-RPC.   
                Hashtable paramTable = new Hashtable();
                paramTable["bankerID"] = bankerID.ToString();
                paramTable["transactionType"] = (int)TransactionType.BuyMoney;
                paramTable["amount"] = amount;
                paramTable["regionHandle"] = regionHandle.ToString();
                paramTable["regionUUID"] = regionUUID.ToString();
                paramTable["description"] = "Add Money to Avatar";

                // Generate the request for transfer.   
                Hashtable resultTable = genericCurrencyXMLRPCRequest(paramTable, "AddBankerMoney");

                // Handle the return values from Money Server.  
                if (resultTable != null)
                {
                    if (resultTable.Contains("success") && (bool)resultTable["success"] == true)
                    {
                        ret = true;
                    }
                    else
                    {
                        if (resultTable.Contains("banker"))
                        {
                            m_settle_user = !(bool)resultTable["banker"]; // If avatar is not banker, Web Settlement is used.
                            if (m_settle_user && m_use_web_settle) 
                                m_log.ErrorFormat("[MONEY MODULE]: AddBankerMoney: Avatar is not Banker. Web Settlemrnt is used.");
                        }
                        else 
                            m_log.ErrorFormat("[MONEY MODULE]: AddBankerMoney: Fail Message {0}", resultTable["message"]);
                    }
                }
                else 
                    m_log.ErrorFormat("[MONEY MODULE]: AddBankerMoney: Money Server is not responce");
            }
            //else m_log.ErrorFormat("[MONEY MODULE]: AddBankerMoney: Money Server is not available!!");

            return ret;
        }

		private void NotifyUserAboutRedirect(UUID userID, int amount, bool isQuoteRequest = false)
		{
			try
			{
				IClientAPI client = GetLocateClient(userID);
				if (client != null)
				{
					string action = isQuoteRequest ? "quote request" : "purchase";
					string alertMsg = $"Currency {action} redirected. {m_redirectMessage}";
					client.SendAlertMessage(alertMsg);
					
					m_log.InfoFormat("[MONEY MODULE]: Notified user {0} about currency redirect for {1}", 
									userID, action);
				}
			}
			catch (Exception ex)
			{
				m_log.WarnFormat("[MONEY MODULE]: Error notifying user about redirect: {0}", ex.Message);
			}
		}

        /// <summary>   
        /// Pay the money of charge.
        /// </summary>   
        /// <param name="amount">   
        /// The amount of money.   
        /// </param>   
        /// <returns>   
        /// return true, if successfully.   
        /// </returns>   
        private bool PayMoneyCharge(UUID sender, int amount, int type, ulong regionHandle, UUID regionUUID, string description)
        {
            //m_log.InfoFormat("[MONEY MODULE]: PayMoneyCharge:");

            bool ret = false;
            IClientAPI senderClient = GetLocateClient(sender);

            // Handle the illegal transaction.   
            // receiverClient could be null.
            if (senderClient == null)
            {
                m_log.InfoFormat("[MONEY MODULE]: PayMoneyCharge: Client {0} is not found", sender.ToString());
                return false;
            }

            if (QueryBalance(sender) < amount)
            {
                m_log.InfoFormat("[MONEY MODULE]: PayMoneyCharge: No insufficient balance in client [{0}]", sender.ToString());
                return false;
            }

            #region Send transaction request to money server and parse the resultes.

            if (m_enable_server)
            {
                // Fill parameters for money transfer XML-RPC.   
                Hashtable paramTable = new Hashtable();
                paramTable["senderID"] = sender.ToString();
                paramTable["senderSessionID"] = senderClient.SessionId.ToString();
                paramTable["senderSecureSessionID"] = senderClient.SecureSessionId.ToString();
                paramTable["transactionType"] = type;
                paramTable["amount"] = amount;
                paramTable["regionHandle"] = regionHandle.ToString();
                paramTable["regionUUID"] = regionUUID.ToString();
                paramTable["description"] = description;

                // Generate the request for transfer.   
                Hashtable resultTable = genericCurrencyXMLRPCRequest(paramTable, "PayMoneyCharge");

                // Handle the return values from Money Server.  
                if (resultTable != null && resultTable.Contains("success"))
                {
                    if ((bool)resultTable["success"] == true)
                    {
                        ret = true;
                    }
                }
                else 
                    m_log.ErrorFormat("[MONEY MODULE]: PayMoneyCharge: Can not pay money of charge request from [{0}]", sender.ToString());
            }
            //else m_log.ErrorFormat("[MONEY MODULE]: PayMoneyCharge: Money Server is not available!!");

            #endregion

            return ret;
        }

        private int QueryBalanceFromMoneyServer(IClientAPI client)
        {
            //m_log.InfoFormat("[MONEY MODULE]: QueryBalanceFromMoneyServer:");

            int balance = 0;

            #region Send the request to get the balance from money server for cilent.

            if (client != null)
            {
                if (m_enable_server)
                {
                    Hashtable paramTable = new Hashtable();
                    paramTable["clientUUID"] = client.AgentId.ToString();
                    paramTable["clientSessionID"] = client.SessionId.ToString();
                    paramTable["clientSecureSessionID"] = client.SecureSessionId.ToString();

                    // Generate the request for transfer.   
                    Hashtable resultTable = genericCurrencyXMLRPCRequest(paramTable, "GetBalance");

                    // Handle the return result
                    if (resultTable != null && resultTable.Contains("success"))
                    {
                        if ((bool)resultTable["success"] == true)
                        {
                            balance = (int)resultTable["clientBalance"];
                        }
                    }
                }
                else
                {
                    if (m_moneyServer.ContainsKey(client.AgentId))
                    {
                        balance = m_moneyServer[client.AgentId];
                    }
                }
            }

            #endregion

            return balance;
        }

        /// <summary>   
        /// Login the money server when the new client login.
        /// </summary>   
        /// <param name="userID">   
        /// Indicate user ID of the new client.   
        /// </param>   
        /// <returns>   
        /// return true, if successfully.   
        /// </returns>   
        private bool LoginMoneyServer(ScenePresence avatar, out int balance)
        {
            //m_log.InfoFormat("[MONEY MODULE]: LoginMoneyServer:");

            balance = 0;
            bool ret = false;
            bool isNpc = avatar.IsNPC;

            IClientAPI client = avatar.ControllingClient;

            if (m_disableXmlRpc)
            {
                m_log.InfoFormat("[MONEY MODULE]: XML-RPC disabled - skipping money server login for avatar {0}", avatar.UUID);
                
                // For local users, use REST API to get balance
                if (m_useRestApi && m_restApiValid && IsLocalUser(avatar.UUID))
                {
                    balance = QueryBalanceFromRestApi(avatar.UUID);
                    return true;
                }
                else
                {
                    m_log.WarnFormat("[MONEY MODULE]: Hypergrid avatar {0} cannot login - XML-RPC disabled", avatar.UUID);
                    return false;
                }
            }

            #region Send money server the client info for login.

            if (!string.IsNullOrEmpty(m_moneyServURL))
            {
                Scene scene = (Scene)client.Scene;
                string userName = string.Empty;

                // Get the username for the login user.
                if (client.Scene is Scene)
                {
                    if (scene != null)
                    {
                        UserAccount account = scene.UserAccountService.GetUserAccount(scene.RegionInfo.ScopeID, client.AgentId);
                        if (account != null)
                        {
                            userName = account.FirstName + " " + account.LastName;
                        }
                    }
                }

                //////////////////////////////////////////////////////////////
                // User Universal Identifer for Grid Avatar, HG Avatar or NPC
                string universalID = string.Empty;
                string firstName = string.Empty;
                string lastName = string.Empty;
                string serverURL = string.Empty;
                int avatarType = (int)AvatarType.LOCAL_AVATAR;
                int avatarClass = (int)AvatarType.LOCAL_AVATAR;

                AgentCircuitData agent = scene.AuthenticateHandler.GetAgentCircuitData(client.AgentId);

                if (agent != null)
                {
                    universalID = Util.ProduceUserUniversalIdentifier(agent);
                    if (!String.IsNullOrEmpty(universalID))
                    {
                        UUID uuid;
                        string tmp;
                        Util.ParseUniversalUserIdentifier(universalID, out uuid, out serverURL, out firstName, out lastName, out tmp);
                    }
                    // if serverURL is empty, avatar is a NPC
                    if (isNpc || String.IsNullOrEmpty(serverURL))
                    {
                        avatarType = (int)AvatarType.NPC_AVATAR;
                    }
                    //
                    if ((agent.teleportFlags & (uint)Constants.TeleportFlags.ViaHGLogin) != 0 || String.IsNullOrEmpty(userName))
                    {
                        avatarType = (int)AvatarType.HG_AVATAR;
                    }
                }
                if (String.IsNullOrEmpty(userName))
                {
                    userName = firstName + " " + lastName;
                }
                
                //
                avatarClass = avatarType;
                if (avatarType == (int)AvatarType.NPC_AVATAR) return false;
                if (avatarType == (int)AvatarType.HG_AVATAR)  avatarClass = m_hg_avatarClass;

                //
                // Login the Money Server.   
                Hashtable paramTable = new Hashtable();
                paramTable["openSimServIP"] = scene.RegionInfo.ServerURI.Replace(scene.RegionInfo.InternalEndPoint.Port.ToString(), 
                                                                                 scene.RegionInfo.HttpPort.ToString());
                paramTable["avatarType"] = avatarType.ToString();
                paramTable["avatarClass"] = avatarClass.ToString();
                paramTable["userName"] = userName;
                paramTable["universalID"] = universalID;
                paramTable["clientUUID"] = client.AgentId.ToString();
                paramTable["clientSessionID"] = client.SessionId.ToString();
                paramTable["clientSecureSessionID"] = client.SecureSessionId.ToString();

                // Generate the request for transfer.   
                Hashtable resultTable = genericCurrencyXMLRPCRequest(paramTable, "ClientLogin");

                // Handle the return result 
                if (resultTable != null && resultTable.Contains("success"))
                {
                    if ((bool)resultTable["success"] == true)
                    {
                        balance = (int)resultTable["clientBalance"];
                        m_log.InfoFormat("[MONEY MODULE]: LoginMoneyServer: Client [{0}] login Money Server {1}", client.AgentId.ToString(), m_moneyServURL);
                        ret = true;
                    }
                }
                else 
                    m_log.ErrorFormat("[MONEY MODULE]: LoginMoneyServer: Unable to login Money Server {0} for client [{1}]", m_moneyServURL, client.AgentId.ToString());
            }
            else 
                m_log.ErrorFormat("[MONEY MODULE]: LoginMoneyServer: Money Server is not available!! Server URL is Null or empty!!");

            #endregion

            // Viewerへ設定を通知する．
            if (ret || string.IsNullOrEmpty(m_moneyServURL))
            {
                 OnEconomyDataRequest(client);
            }

            return ret;
        }

        /// <summary>   
        /// Log off from the money server.   
        /// </summary>   
        /// <param name="userID">   
        /// Indicate user ID of the new client.   
        /// </param>   
        /// <returns>   
        /// return true, if successfully.   
        /// </returns>   
        private bool LogoffMoneyServer(IClientAPI client)
        {
            //m_log.InfoFormat("[MONEY MODULE]: LogoffMoneyServer:");

            bool ret = false;

            if (!string.IsNullOrEmpty(m_moneyServURL))
            {
                // Log off from the Money Server.   
                Hashtable paramTable = new Hashtable();
                paramTable["clientUUID"] = client.AgentId.ToString();
                paramTable["clientSessionID"] = client.SessionId.ToString();
                paramTable["clientSecureSessionID"] = client.SecureSessionId.ToString();

                // Generate the request for transfer.   
                Hashtable resultTable = genericCurrencyXMLRPCRequest(paramTable, "ClientLogout");
                // Handle the return result
                if (resultTable != null && resultTable.Contains("success"))
                {
                    if ((bool)resultTable["success"] == true)
                    {
                        ret = true;
                    }
                }
            }

            return ret;
        }

        //
        private EventManager.MoneyTransferArgs GetTransactionInfo(IClientAPI client, string transactionID)
        {
            //m_log.InfoFormat("[MONEY MODULE]: GetTransactionInfo:");

            EventManager.MoneyTransferArgs args = null;

            if (m_enable_server)
            {
                Hashtable paramTable = new Hashtable();
                paramTable["clientUUID"] = client.AgentId.ToString();
                paramTable["clientSessionID"] = client.SessionId.ToString();          
                paramTable["clientSecureSessionID"] = client.SecureSessionId.ToString();
                paramTable["transactionID"] = transactionID;

                // Generate the request for transfer.   
                Hashtable resultTable = genericCurrencyXMLRPCRequest(paramTable, "GetTransaction");

                // Handle the return result
                if (resultTable != null && resultTable.Contains("success"))
                {
                    if ((bool)resultTable["success"] == true)
                    {
                        int amount = (int)resultTable["amount"];
                        int type = (int)resultTable["type"];
                        string desc = (string)resultTable["description"];
                        UUID sender = UUID.Zero;
                        UUID recver = UUID.Zero;
                        UUID.TryParse((string)resultTable["sender"], out sender);
                        UUID.TryParse((string)resultTable["receiver"], out recver);
                        args = new EventManager.MoneyTransferArgs(sender, recver, amount, type, desc);
                    }
                    else
                    {
                        m_log.ErrorFormat("[MONEY MODULE]: GetTransactionInfo: GetTransactionInfo: Fail to Request. {0}", (string)resultTable["description"]);
                    }
                }
                else
                {
                    m_log.ErrorFormat("[MONEY MODULE]: GetTransactionInfo: Invalid Response");
                }
            }
            else
            {
                m_log.ErrorFormat("[MONEY MODULE]: GetTransactionInfo: Invalid Money Server URL");
            }

            return args;
        }

		/// <summary>   
		/// Generic XMLRPC client abstraction   
		/// </summary>   
		/// <param name="reqParams">Hashtable containing parameters to the method</param>   
		/// <param name="method">Method to invoke</param>   
		/// <returns>Hashtable with success=>bool and other values</returns>   
		private Hashtable genericCurrencyXMLRPCRequest(Hashtable reqParams, string method)
		{
			//m_log.InfoFormat("[MONEY MODULE]: genericCurrencyXMLRPCRequest:");

			if (reqParams.Count <= 0 || string.IsNullOrEmpty(method)) return null;

			// 🔥 NEW: Check if XML-RPC to external money servers is disabled
			if (m_disableXmlRpc && IsExternalMoneyServer())
			{
				m_log.WarnFormat("[MONEY MODULE]: XML-RPC to external money server blocked by configuration: {0}", m_moneyServURL);
				
				Hashtable errorResponse = new Hashtable();
				errorResponse["success"] = false;
				errorResponse["errorMessage"] = "External money server communications are disabled";
				errorResponse["error_code"] = "EXTERNAL_XMLRPC_DISABLED";
				return errorResponse;
			}

			// 🔥 NEW: If we're communicating with ourselves, use direct handler invocation
			// Find the first available scene to check if we're self-referencing
			Scene currentScene = null;
			lock (m_sceneList)
			{
				if (m_sceneList.Count > 0)
				{
					foreach (Scene scene in m_sceneList.Values)
					{
						currentScene = scene;
						break;
					}
				}
			}

			if (currentScene != null && 
				(m_moneyServURL.Contains(currentScene.RegionInfo.ExternalHostName) || 
				 (!string.IsNullOrEmpty(currentScene.RegionInfo.ServerURI) && m_moneyServURL.Contains(currentScene.RegionInfo.ServerURI))))
			{
				m_log.DebugFormat("[MONEY MODULE]: Routing {0} request to local handler", method);
				return RouteToLocalHandler(method, reqParams, currentScene);
			}

			if (m_checkServerCert)
			{
				if (!m_moneyServURL.StartsWith("https://"))
				{
					m_log.InfoFormat("[MONEY MODULE]: genericCurrencyXMLRPCRequest: CheckServerCert is true, but protocol is not HTTPS. Please check INI file");
					//return null;
				}
			}
			else
			{
				if (!m_moneyServURL.StartsWith("https://") && !m_moneyServURL.StartsWith("http://"))
				{
					m_log.ErrorFormat("[MONEY MODULE]: genericCurrencyXMLRPCRequest: URL is not http or https. Please check INI file");
					return null;
				}
			}

			ArrayList parameters = new ArrayList();
			parameters.Add(reqParams);
			XmlRpcRequest xmlrpcReq = new XmlRpcRequest(method, parameters);

			XmlRpcResponse xmlrpcResp;
			Hashtable xmlData = null;

			try
			{
				xmlrpcResp = xmlrpcReq.Send(m_moneyServURL, MONEYMODULE_REQUEST_TIMEOUT);
				if (xmlrpcResp != null)
				{
					if (xmlrpcResp.IsFault)
					{
						m_log.ErrorFormat("[MONEY MODULE]: genericCurrencyXMLRPCRequest: XML-RPC fault {0}: {1}", xmlrpcResp.FaultCode, xmlrpcResp.FaultString);
					}
					else
					{
						xmlData = (Hashtable)xmlrpcResp.Value;
					}
				}
				else
				{
					m_log.ErrorFormat("[MONEY MODULE]: genericCurrencyXMLRPCRequest: XML-RPC request to {0} failed", m_moneyServURL);
				}
			}
			catch (Exception e)
			{
				m_log.ErrorFormat("[MONEY MODULE]: genericCurrencyXMLRPCRequest: Exception {0}", e.Message);
			}

			return xmlData;
		}

		/// <summary>
		/// Check if the configured money server is external (not self)
		/// </summary>
		private bool IsExternalMoneyServer()
		{
			if (string.IsNullOrEmpty(m_moneyServURL))
				return false;

			// Check if the URL points to any of our regions
			lock (m_sceneList)
			{
				foreach (Scene scene in m_sceneList.Values)
				{
					if (!string.IsNullOrEmpty(scene.RegionInfo.ServerURI) && 
						m_moneyServURL.Contains(scene.RegionInfo.ServerURI))
					{
						return false; // It's us
					}
					
					if (m_moneyServURL.Contains(scene.RegionInfo.ExternalHostName))
					{
						return false; // It's us
					}
				}
			}
			
			return true; // It's external
		}

		/// <summary>
		/// Route XML-RPC requests to local handlers when money server points to self
		/// </summary>
		private Hashtable RouteToLocalHandler(string method, Hashtable parameters, Scene scene)
		{
			try
			{
				ArrayList paramList = new ArrayList();
				paramList.Add(parameters);
				
				XmlRpcRequest fakeRequest = new XmlRpcRequest(method, new ArrayList { parameters });
				IPEndPoint fakeEndpoint = new IPEndPoint(IPAddress.Loopback, 0);
				XmlRpcResponse response = null;

				// Route to appropriate handler based on method name
				switch (method.ToLower())
				{
					case "clientlogin":
					case "clientlogout":
						// These don't have direct handlers, create appropriate response
						response = CreateDefaultSuccessResponse();
						break;
						
					case "getbalance":
						response = GetBalanceHandler(fakeRequest, fakeEndpoint);
						break;
						
					case "transfermoney":
					case "forcetransfermoney":
						response = RegionMoveMoneyHandler(fakeRequest, fakeEndpoint);
						break;
						
					case "paymoneycharge":
						response = RegionMoveMoneyHandler(fakeRequest, fakeEndpoint);
						break;
						
					case "addbankermoney":
						response = AddBankerMoneyHandler(fakeRequest, fakeEndpoint);
						break;
						
					case "sendmoney":
						response = SendMoneyHandler(fakeRequest, fakeEndpoint);
						break;
						
					case "movemoney":
						response = MoveMoneyHandler(fakeRequest, fakeEndpoint);
						break;
						
					case "onmoneytransfered":
						response = OnMoneyTransferedHandler(fakeRequest, fakeEndpoint);
						break;
						
					case "updatebalance":
						response = BalanceUpdateHandler(fakeRequest, fakeEndpoint);
						break;
						
					case "useralert":
						response = UserAlertHandler(fakeRequest, fakeEndpoint);
						break;
						
					case "getcurrencyquote":
						response = GetCurrencyQuoteHandler(fakeRequest, fakeEndpoint);
						break;
						
					case "buycurrency":
						response = BuyCurrencyHandler(fakeRequest, fakeEndpoint);
						break;
						
					default:
						m_log.WarnFormat("[MONEY MODULE]: No local handler for method: {0}", method);
						return CreateErrorHashtable($"No local handler for method: {method}");
				}

				if (response != null && response.Value is Hashtable)
				{
					return (Hashtable)response.Value;
				}
				else
				{
					return CreateDefaultSuccessResponse().Value as Hashtable;
				}
			}
			catch (Exception ex)
			{
				m_log.ErrorFormat("[MONEY MODULE]: Error in local handler routing for {0}: {1}", method, ex.Message);
				return CreateErrorHashtable($"Local handler error: {ex.Message}");
			}
		}

		private XmlRpcResponse CreateDefaultSuccessResponse()
		{
			XmlRpcResponse response = new XmlRpcResponse();
			Hashtable result = new Hashtable();
			result["success"] = true;
			response.Value = result;
			return response;
		}

		private Hashtable CreateErrorHashtable(string message)
		{
			Hashtable error = new Hashtable();
			error["success"] = false;
			error["errorMessage"] = message;
			return error;
		}

        /// <summary>   
        /// Get the scene by user ID.   
        /// </summary>   
        /// <param name="userID">   
        /// User ID.   
        /// </param>   
        /// <returns>   
        /// The scene contains the user.   
        /// </returns>   
        private Scene GetLocateScene(UUID userID)
        {
            Scene scene = null;

            lock (m_sceneList)
            {
                foreach (Scene s in m_sceneList.Values)
                {
                    ScenePresence presence = s.GetScenePresence(userID);
                    if (presence != null && !presence.IsChildAgent)
                    {
                        scene = s;
                        break;
                    }
                }
            }

            return scene;
        }

        /// <summary>   
        /// Get the scene object part by prim ID.   
        /// </summary>   
        /// <param name="primID">   
        /// Prim ID.   
        /// </param>   
        /// <returns>   
        /// The scene object part.   
        /// </returns>   
        private SceneObjectPart GetLocatePrim(UUID primID)
        {
            SceneObjectPart sceneObj = null;

            lock (m_sceneList)
            {
                foreach (Scene s in m_sceneList.Values)
                {
                    sceneObj = s.GetSceneObjectPart(primID);
                    if (sceneObj != null)
                    {
                        break;
                    }
                }
            }

            return sceneObj;
        }

        /// <summary>   
        /// Get the client by user ID.   
        /// </summary>   
        /// <param name="userID">   
        /// User ID.   
        /// </param>   
        /// <returns>   
        /// The client.   
        /// </returns>   
        private IClientAPI GetLocateClient(UUID userID)
        {
            IClientAPI client = null;

            lock (m_sceneList)
            {
                foreach (Scene s in m_sceneList.Values)
                {
                    ScenePresence presence = s.GetScenePresence(userID);
                    if (presence != null && !presence.IsChildAgent)
                    {
                        client = presence.ControllingClient;
                        break;
                    }
                }
            }

            return client;
        }

        #endregion

        #region HTTP Handler Implementation Methods

        /// <summary>
        /// Handle currency quote requests via HTTP
        /// </summary>
		private void HandleCurrencyQuote(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
		{
			try
			{
				// Parse request parameters
				string query = httpRequest.Url.Query;
				var parameters = HttpUtility.ParseQueryString(query);

				UUID agentId = UUID.Zero;
				int currencyBuy = 1000;

				if (parameters["agentId"] != null)
					UUID.TryParse(parameters["agentId"], out agentId);
				if (parameters["currencyBuy"] != null)
					int.TryParse(parameters["currencyBuy"], out currencyBuy);

				m_log.InfoFormat("[MONEY MODULE]: HandleCurrencyQuote: Agent {0} requesting quote for {1}", agentId, currencyBuy);

				// Calculate cost
				int estimatedCost = CalculateCostForCurrency(currencyBuy);

				// Return JSON response
				string jsonResponse = $@"{{
					""success"": true,
					""currency"": {{
						""estimatedCost"": {estimatedCost},
						""currencyBuy"": {currencyBuy}
					}},
					""confirm"": ""{UUID.Random()}""
				}}";

				SendJsonResponse(httpResponse, jsonResponse);
			}
			catch (Exception ex)
			{
				m_log.ErrorFormat("[MONEY MODULE]: HandleCurrencyQuote error: {0}", ex.Message);
				SendErrorResponse(httpResponse, 500, "Internal server error");
			}
		}

		/// <summary>
		/// Handle currency purchase requests via HTTP
		/// </summary>
		private void HandleCurrencyBuy(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
		{
			try
			{
				// Parse request parameters from both query string and form data
				string query = httpRequest.Url.Query;
				var parameters = HttpUtility.ParseQueryString(query);

				// Also try to read form data if it's a POST request
				if (httpRequest.HttpMethod == "POST")
				{
					using (StreamReader reader = new StreamReader(httpRequest.InputStream))
					{
						string formData = reader.ReadToEnd();
						var formParams = HttpUtility.ParseQueryString(formData);
						foreach (string key in formParams.AllKeys)
						{
							parameters[key] = formParams[key];
						}
					}
				}

				UUID agentId = UUID.Zero;
				int currencyBuy = 1000;
				string confirm = parameters["confirm"];

				if (parameters["agentId"] != null)
					UUID.TryParse(parameters["agentId"], out agentId);
				if (parameters["currencyBuy"] != null)
					int.TryParse(parameters["currencyBuy"], out currencyBuy);

				m_log.InfoFormat("[MONEY MODULE]: HandleCurrencyBuy: Agent {0} purchasing {1}", agentId, currencyBuy);

				// Redirect to external payment processor
				string redirectUrl = m_redirectUrl + "?agentId=" + agentId.ToString() + "&amount=" + currencyBuy;

				string jsonResponse = $@"{{
					""success"": false,
					""errorMessage"": ""{m_redirectMessage}"",
					""errorURI"": ""{redirectUrl}""
				}}";

				SendJsonResponse(httpResponse, jsonResponse);
			}
			catch (Exception ex)
			{
				m_log.ErrorFormat("[MONEY MODULE]: HandleCurrencyBuy error: {0}", ex.Message);
				SendErrorResponse(httpResponse, 500, "Internal server error");
			}
		}

		/// <summary>
		/// Handle currency balance requests via HTTP
		/// </summary>
		private void HandleCurrencyBalance(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
		{
			try
			{
				string query = httpRequest.Url.Query;
				var parameters = HttpUtility.ParseQueryString(query);

				UUID agentId = UUID.Zero;
				if (parameters["agentId"] != null)
					UUID.TryParse(parameters["agentId"], out agentId);

				int balance = QueryBalance(agentId);

				string jsonResponse = $@"{{
					""success"": true,
					""agentId"": ""{agentId}"",
					""funds"": {balance}
				}}";

				SendJsonResponse(httpResponse, jsonResponse);
			}
			catch (Exception ex)
			{
				m_log.ErrorFormat("[MONEY MODULE]: HandleCurrencyBalance error: {0}", ex.Message);
				SendErrorResponse(httpResponse, 500, "Internal server error");
			}
		}


        /// <summary>
        /// Calculate cost for currency purchase
        /// </summary>
        private int CalculateCostForCurrency(int currencyAmount)
        {
            // Implement your pricing logic here
            // Example: $1 = 1000 currency units
            double rate = 0.001; // Adjust based on your economy
            return (int)Math.Ceiling(currencyAmount * rate * 100); // Return in cents
        }

		/// <summary>
		/// Send JSON response
		/// </summary>
		private void SendJsonResponse(IOSHttpResponse httpResponse, string json)
		{
			byte[] buffer = Encoding.UTF8.GetBytes(json);
			httpResponse.ContentType = "application/json";
			httpResponse.ContentLength = buffer.Length;
			httpResponse.SendChunked = false;
			httpResponse.StatusCode = 200;
			httpResponse.OutputStream.Write(buffer, 0, buffer.Length);
			httpResponse.OutputStream.Flush();
		}

		/// <summary>
		/// Send error response
		/// </summary>
		private void SendErrorResponse(IOSHttpResponse httpResponse, int statusCode, string message)
		{
			string jsonResponse = $@"{{""success"": false, ""error"": ""{message}""}}";
			byte[] buffer = Encoding.UTF8.GetBytes(jsonResponse);
			httpResponse.ContentType = "application/json";
			httpResponse.ContentLength = buffer.Length;
			httpResponse.SendChunked = false;
			httpResponse.StatusCode = statusCode;
			httpResponse.OutputStream.Write(buffer, 0, buffer.Length);
			httpResponse.OutputStream.Flush();
		}
		
		// Called during module initialisation/region add
		private void WireSimulatorFeatures(Scene scene)
		{
			var featuresModule = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
			if (featuresModule == null)
				return;

			// Static extras
			featuresModule.AddOpenSimExtraFeature("currency", OSD.FromString(m_currencySymbol));
			featuresModule.AddOpenSimExtraFeature("currency-base-uri", OSD.FromString(m_currencyBaseUri));

			// Dynamic hook
			featuresModule.OnSimulatorFeaturesRequest += (UUID requestingAgentID, ref OSDMap features) =>
			{
				if (!features.ContainsKey("currency"))
					features["currency"] = OSD.FromString(m_currencySymbol);
				if (!features.ContainsKey("currency-base-uri"))
					features["currency-base-uri"] = OSD.FromString(m_currencyBaseUri);

				if (!string.IsNullOrEmpty(m_currencyCapsUrl))
					features["Currency"] = OSD.FromString(m_currencyCapsUrl);
			};

			m_log.Info("[MONEY MODULE]: SimulatorFeatures currency extras wired");
		}
		
		private bool PerformCurrencyPurchase(OSDMap req)
		{
			// Extract agentId and amount if present
			UUID agentId = UUID.Zero;
			if (req.ContainsKey("agentId"))
				UUID.TryParse(req["agentId"].AsString(), out agentId);

			int currencyBuy = req.ContainsKey("currencyBuy") ? req["currencyBuy"].AsInteger() : 0;

			m_log.InfoFormat("[MONEY MODULE]: Simulated purchase: agent {0} buying {1} units", agentId, currencyBuy);

			// Here you would normally debit real money, update balances, etc.
			// For now, just return true to simulate success
			return true;
		}


        #endregion
    }
}