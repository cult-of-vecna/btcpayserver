using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Crowdfund;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Plugins.PointOfSale.Models;
using BTCPayServer.Rating;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using LNURL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LightningAddressData = BTCPayServer.Data.LightningAddressData;
using MarkPayoutRequest = BTCPayServer.HostedServices.MarkPayoutRequest;

namespace BTCPayServer
{
    [Route("~/{cryptoCode}/[controller]/")]
    [Route("~/{cryptoCode}/lnurl/")]
    public class UILNURLController : Controller
    {
        private readonly InvoiceRepository _invoiceRepository;
        private readonly EventAggregator _eventAggregator;
        private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
        private readonly LightningLikePaymentHandler _lightningLikePaymentHandler;
        private readonly StoreRepository _storeRepository;
        private readonly AppService _appService;
        private readonly UIInvoiceController _invoiceController;
        private readonly LinkGenerator _linkGenerator;
        private readonly LightningAddressService _lightningAddressService;
        private readonly LightningLikePayoutHandler _lightningLikePayoutHandler;
        private readonly PullPaymentHostedService _pullPaymentHostedService;
        private readonly BTCPayNetworkJsonSerializerSettings _btcPayNetworkJsonSerializerSettings;
        private readonly IPluginHookService _pluginHookService;
        private readonly InvoiceActivator _invoiceActivator;
        private readonly IMemoryCache _memoryCache;
        private readonly RateFetcher _rateFetcher;

        public UILNURLController(InvoiceRepository invoiceRepository,
            EventAggregator eventAggregator,
            BTCPayNetworkProvider btcPayNetworkProvider,
            LightningLikePaymentHandler lightningLikePaymentHandler,
            StoreRepository storeRepository,
            AppService appService,
            UIInvoiceController invoiceController,
            LinkGenerator linkGenerator,
            LightningAddressService lightningAddressService,
            LightningLikePayoutHandler lightningLikePayoutHandler,
            PullPaymentHostedService pullPaymentHostedService,
            BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings,
            IPluginHookService pluginHookService,
            InvoiceActivator invoiceActivator,
            IMemoryCache memoryCache,
            RateFetcher rateFetcher)
        {
            _invoiceRepository = invoiceRepository;
            _eventAggregator = eventAggregator;
            _btcPayNetworkProvider = btcPayNetworkProvider;
            _lightningLikePaymentHandler = lightningLikePaymentHandler;
            _storeRepository = storeRepository;
            _appService = appService;
            _invoiceController = invoiceController;
            _linkGenerator = linkGenerator;
            _lightningAddressService = lightningAddressService;
            _lightningLikePayoutHandler = lightningLikePayoutHandler;
            _pullPaymentHostedService = pullPaymentHostedService;
            _btcPayNetworkJsonSerializerSettings = btcPayNetworkJsonSerializerSettings;
            _pluginHookService = pluginHookService;
            _invoiceActivator = invoiceActivator;
            _memoryCache = memoryCache;
            _rateFetcher = rateFetcher;
        }

        [HttpGet("withdraw/pp/{pullPaymentId}")]
        public async Task<IActionResult> GetLNURLForPullPayment(string cryptoCode, string pullPaymentId, string pr, CancellationToken cancellationToken)
        {
            var network = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>(cryptoCode);
            if (network is null || !network.SupportLightning)
            {
                return NotFound();
            }

            var pmi = new PaymentMethodId(cryptoCode, PaymentTypes.LightningLike);
            var pp = await _pullPaymentHostedService.GetPullPayment(pullPaymentId, true);
            if (!pp.IsRunning() || !pp.IsSupported(pmi))
            {
                return NotFound();
            }

            var blob = pp.GetBlob();
            if (!_pullPaymentHostedService.SupportsLNURL(blob))
            {
                return NotFound();
            }

            var unit = blob.Currency == "SATS" ? LightMoneyUnit.Satoshi : LightMoneyUnit.BTC;
            var progress = _pullPaymentHostedService.CalculatePullPaymentProgress(pp, DateTimeOffset.UtcNow);
            var remaining = progress.Limit - progress.Completed - progress.Awaiting;
            var request = new LNURLWithdrawRequest
            {
                MaxWithdrawable = LightMoney.FromUnit(remaining, unit),
                K1 = pullPaymentId,
                BalanceCheck = new Uri(Request.GetCurrentUrl()),
                CurrentBalance = LightMoney.FromUnit(remaining, unit),
                MinWithdrawable =
                    LightMoney.FromUnit(
                        Math.Min(await _lightningLikePayoutHandler.GetMinimumPayoutAmount(pmi, null), remaining),
                        unit),
                Tag = "withdrawRequest",
                Callback = new Uri(Request.GetCurrentUrl()),
                // It's not `pp.GetBlob().Description` because this would be HTML
                // and LNUrl UI's doesn't expect HTML there
                DefaultDescription = pp.GetBlob().Name ?? string.Empty,
            };
            if (pr is null)
            {
                return Ok(request);
            }

            if (!BOLT11PaymentRequest.TryParse(pr, out var result, network.NBitcoinNetwork) || result is null)
            {
                return BadRequest(new LNUrlStatusResponse { Status = "ERROR", Reason = "Payment request was not a valid BOLT11" });
            }

            if (result.MinimumAmount < request.MinWithdrawable || result.MinimumAmount > request.MaxWithdrawable)
                return BadRequest(new LNUrlStatusResponse { Status = "ERROR", Reason = $"Payment request was not within bounds ({request.MinWithdrawable.ToUnit(LightMoneyUnit.Satoshi)} - {request.MaxWithdrawable.ToUnit(LightMoneyUnit.Satoshi)} sats)" });
            var store = await _storeRepository.FindStore(pp.StoreId);
            var pm = store!.GetSupportedPaymentMethods(_btcPayNetworkProvider)
                .OfType<LightningSupportedPaymentMethod>()
                .FirstOrDefault(method => method.PaymentId == pmi);
            if (pm is null)
            {
                return NotFound();
            }

            var claimResponse = await _pullPaymentHostedService.Claim(new ClaimRequest
            {
                Destination = new BoltInvoiceClaimDestination(pr, result),
                PaymentMethodId = pmi,
                PullPaymentId = pullPaymentId,
                StoreId = pp.StoreId,
                Value = result.MinimumAmount.ToDecimal(unit)
            });

            if (claimResponse.Result != ClaimRequest.ClaimResult.Ok)
                return BadRequest(new LNUrlStatusResponse { Status = "ERROR", Reason = "Payment request could not be paid" });

            switch (claimResponse.PayoutData.State)
            {
                case PayoutState.AwaitingPayment:
                    {
                        var client =
                            _lightningLikePaymentHandler.CreateLightningClient(pm, network);
                        var payResult = await UILightningLikePayoutController.TrypayBolt(client,
                            claimResponse.PayoutData.GetBlob(_btcPayNetworkJsonSerializerSettings),
                            claimResponse.PayoutData, result, pmi, cancellationToken);

                        switch (payResult.Result)
                        {
                            case PayResult.Ok:
                            case PayResult.Unknown:
                                await _pullPaymentHostedService.MarkPaid(new MarkPayoutRequest
                                {
                                    PayoutId = claimResponse.PayoutData.Id,
                                    State = claimResponse.PayoutData.State,
                                    Proof = claimResponse.PayoutData.GetProofBlobJson()
                                });

                                return Ok(new LNUrlStatusResponse
                                {
                                    Status = "OK",
                                    Reason = payResult.Message
                                });
                            case PayResult.CouldNotFindRoute:
                            case PayResult.Error:
                            default:
                                await _pullPaymentHostedService.Cancel(
                                    new PullPaymentHostedService.CancelRequest(new[]
                                        { claimResponse.PayoutData.Id }, null));

                                return BadRequest(new LNUrlStatusResponse
                                {
                                    Status = "ERROR",
                                    Reason = payResult.Message ?? payResult.Result.ToString()
                                });
                        }
                    }
                case PayoutState.AwaitingApproval:
                    return Ok(new LNUrlStatusResponse
                    {
                        Status = "OK",
                        Reason =
                            "The payment request has been recorded, but still needs to be approved before execution."
                    });
                case PayoutState.InProgress:
                case PayoutState.Completed:
                    return Ok(new LNUrlStatusResponse { Status = "OK" });
                case PayoutState.Cancelled:
                    return BadRequest(new LNUrlStatusResponse { Status = "ERROR", Reason = "Payment request could not be paid" });
            }

            return Ok(request);
        }

        [HttpGet("pay/app/{appId}/{itemCode}")]
        public async Task<IActionResult> GetLNURLForApp(string cryptoCode, string appId, string itemCode = null)
        {
            if (!NetworkSupportsLightning(cryptoCode, out _))
                return null;
            var app = await _appService.GetApp(appId, null, true);
            if (app is null)
            {
                return NotFound();
            }

            var store = app.StoreData;
            if (store is null)
            {
                return NotFound();
            }

            if (string.IsNullOrEmpty(itemCode))
            {
                return NotFound();
            }

            ViewPointOfSaleViewModel.Item[] items;
            string currencyCode;
            PointOfSaleSettings posS = null;
            switch (app.AppType)
            {
                case CrowdfundAppType.AppType:
                    var cfS = app.GetSettings<CrowdfundSettings>();
                    currencyCode = cfS.TargetCurrency;
                    items = AppService.Parse(cfS.PerksTemplate);
                    break;
                case PointOfSaleAppType.AppType:
                    posS = app.GetSettings<PointOfSaleSettings>();
                    currencyCode = posS.Currency;
                    items = AppService.Parse(posS.Template);
                    break;
                default:
                    //TODO: Allow other apps to define lnurl support
                    return NotFound();
            }

            ViewPointOfSaleViewModel.Item item = null;
            if (!string.IsNullOrEmpty(itemCode))
            {
                var pmi = GetLNUrlPaymentMethodId(cryptoCode, store, out _);
                if (pmi is null)
                    return NotFound("LNUrl or LN is disabled");
                var escapedItemId = Extensions.UnescapeBackSlashUriString(itemCode);
                item = items.FirstOrDefault(item1 =>
                    item1.Id.Equals(itemCode, StringComparison.InvariantCultureIgnoreCase) ||
                    item1.Id.Equals(escapedItemId, StringComparison.InvariantCultureIgnoreCase));

                if (item is null ||
                    item.Inventory <= 0 ||
                    (item.PaymentMethods?.Any() is true &&
                     item.PaymentMethods?.Any(s => PaymentMethodId.Parse(s) == pmi) is false))
                {
                    return NotFound();
                }
            }
            else if (app.AppType == PointOfSaleAppType.AppType && posS?.ShowCustomAmount is not true)
            {
                return NotFound();
            }

            var createInvoice = new CreateInvoiceRequest()
            {
                Amount = item?.Price.Value,
                Currency = currencyCode,
                Checkout = new InvoiceDataBase.CheckoutOptions()
                {
                    RedirectURL = app.AppType switch
                    {
                        PointOfSaleAppType.AppType => app.GetSettings<PointOfSaleSettings>().RedirectUrl ??
                                                       HttpContext.Request.GetAbsoluteUri($"/apps/{app.Id}/pos"),
                        _ => null
                    }
                }
            };

            var invoiceMetadata = new InvoiceMetadata();
            invoiceMetadata.OrderId = AppService.GetAppOrderId(app);
            if (item != null)
            {
                invoiceMetadata.ItemCode = item.Id;
                invoiceMetadata.ItemDesc = item.Description;
            }
            createInvoice.Metadata = invoiceMetadata.ToJObject();


            return await CreateLNURLRequestWithoutInvoice( new LNURLRequestParams(cryptoCode,
                store,
                store.GetStoreBlob(),
                createInvoice,
                additionalTags: new List<string> { AppService.GetAppInternalTag(appId) },
                allowOverpay: false));
        }

        public class EditLightningAddressVM
        {
            public class EditLightningAddressItem : LightningAddressSettings.LightningAddressItem
            {
                [Required]
                [RegularExpression("[a-zA-Z0-9-_]+")]
                public string Username { get; set; }
            }

            public EditLightningAddressItem Add { get; set; }
            public List<EditLightningAddressItem> Items { get; set; } = new();
        }

        public class LightningAddressSettings
        {
            public class LightningAddressItem
            {
                public string StoreId { get; set; }
                [Display(Name = "Invoice currency")] public string CurrencyCode { get; set; }

                [Display(Name = "Min sats")]
                [Range(1, double.PositiveInfinity)]
                public decimal? Min { get; set; }

                [Display(Name = "Max sats")]
                [Range(1, double.PositiveInfinity)]
                public decimal? Max { get; set; }

                [Display(Name = "Invoice metadata")]
                public string InvoiceMetadata { get; set; }
            }

            public ConcurrentDictionary<string, LightningAddressItem> Items { get; } = new();
            public ConcurrentDictionary<string, string[]> StoreToItemMap { get; } = new();
        }

        [HttpGet("~/.well-known/lnurlp/{username}")]
        [EnableCors(CorsPolicies.All)]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> ResolveLightningAddress(string username)
        {
            var lightningAddressSettings = await _lightningAddressService.ResolveByAddress(username);
            if (lightningAddressSettings is null || username is null)
                return NotFound("Unknown username");

            var store = await _storeRepository.FindStore(lightningAddressSettings.StoreDataId);
            if (store is null)
                return NotFound("Unknown username");

            var blob = lightningAddressSettings.GetBlob();
            return await CreateLNURLRequestWithoutInvoice(new LNURLRequestParams(
                "BTC",
                store,
                store.GetStoreBlob(),
                new CreateInvoiceRequest() {Currency = blob?.CurrencyCode, Metadata = blob?.InvoiceMetadata},
                new LNURLPayRequest()
                {
                    MinSendable = blob?.Min is decimal min ? new LightMoney(min, LightMoneyUnit.Satoshi) : null,
                    MaxSendable = blob?.Max is decimal max ? new LightMoney(max, LightMoneyUnit.Satoshi) : null,
                },
                new Dictionary<string, string>() {{"text/identifier", $"{username}@{Request.Host}"}}));
        }


        [HttpGet("pay")]
        [EnableCors(CorsPolicies.All)]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> GetLNUrlForStore(
            string cryptoCode,
            string storeId,
            string currencyCode = null)
        {
            var store = this.HttpContext.GetStoreData();
            if (store is null)
                return NotFound();

            var blob = store.GetStoreBlob();
            if (!blob.AnyoneCanInvoice)
                return NotFound("'Anyone can invoice' is turned off");
            return await CreateLNURLRequestWithoutInvoice(new LNURLRequestParams(cryptoCode,
                store,
                blob,
                new CreateInvoiceRequest
                {
                    Currency = currencyCode
                }));
        }
        class LNURLRequestParams
        {
            public LNURLRequestParams(string cryptoCode,
                Data.StoreData store,
                Data.StoreBlob blob,
                CreateInvoiceRequest createInvoice,
                LNURLPayRequest lnurlRequest = null,
                Dictionary<string, string> lnUrlMetadata = null,
                List<string> additionalTags = null,
                bool allowOverpay = true)
            {
                CryptoCode = cryptoCode;
                Store = store;
                Blob = blob;
                CreateInvoice = createInvoice;
                LNURLRequest = lnurlRequest;
                LNURLMetadata = lnUrlMetadata;
                AdditionalTags = additionalTags;
                AllowOverpay = allowOverpay;
            }
            public string CryptoCode { get; set; }
            public Data.StoreData Store { get; set; }
            public Data.StoreBlob Blob { get; set; }
            public CreateInvoiceRequest CreateInvoice { get; set; }
            public LNURLPayRequest LNURLRequest { get; set; }
            public Dictionary<string, string> LNURLMetadata { get; set; }
            public List<string> AdditionalTags { get; set; }
            public bool AllowOverpay { get; set; }
        }
        private async Task<IActionResult> CreateLNURLRequestWithoutInvoice(LNURLRequestParams requestParams)
        {
            var pmi = GetLNUrlPaymentMethodId(requestParams.CryptoCode, requestParams.Store, out LNURLPaySupportedPaymentMethod lnUrlMethod);
            if (pmi is null)
                return NotFound("LNUrl or LN is disabled");

            
            var k = Guid.NewGuid();
            
            requestParams.LNURLRequest ??= new LNURLPayRequest();
            requestParams.LNURLMetadata ??= new Dictionary<string, string>();

            // Set the callback endpoint to trigger invoice generation
            requestParams.LNURLRequest.Tag = "payRequest";
            requestParams.LNURLRequest.Callback = new Uri(_linkGenerator.GetUriByAction(
                action: nameof(LNURLCallback),
                controller: "UILNURL",
                values: new { k },
                Request.Scheme, Request.Host, Request.PathBase));
            
            
            if (!requestParams.LNURLMetadata.ContainsKey("text/plain"))
            {
                var invMetadata = InvoiceMetadata.FromJObject(requestParams.CreateInvoice.Metadata);
                var invoiceDescription = requestParams.Blob.LightningDescriptionTemplate
                        .Replace("{StoreName}", requestParams.Store.StoreName ?? "", StringComparison.OrdinalIgnoreCase)
                        .Replace("{ItemDescription}", invMetadata.ItemDesc ?? "", StringComparison.OrdinalIgnoreCase)
                        .Replace("{OrderId}", invMetadata.OrderId ?? "", StringComparison.OrdinalIgnoreCase);
                requestParams.LNURLMetadata.Add("text/plain", invoiceDescription);
            }

            requestParams.LNURLRequest.CommentAllowed = lnUrlMethod.LUD12Enabled ? 2000 : 0;
            requestParams.LNURLRequest.Metadata = JsonConvert.SerializeObject(requestParams.LNURLMetadata.Select(kv => new[] { kv.Key, kv.Value }));
            // We don't think BTCPay handle well 0 sats payments, just in case make it minimum one sat.
            if (requestParams.LNURLRequest.MinSendable is null || requestParams.LNURLRequest.MinSendable < LightMoney.Satoshis(1.0m))
                requestParams.LNURLRequest.MinSendable = LightMoney.Satoshis(1.0m);

            if (requestParams.LNURLRequest.MaxSendable is null)
                requestParams.LNURLRequest.MaxSendable = LightMoney.FromUnit(6.12m, LightMoneyUnit.BTC);

            if (requestParams.CreateInvoice.Type != InvoiceType.TopUp)
            {
                LightMoney cAmount;
                if (requestParams.CreateInvoice.Currency != pmi.CryptoCode)
                {
                    var rr = requestParams.Store.GetStoreBlob().GetRateRules(_btcPayNetworkProvider);
                    var rate = _rateFetcher.FetchRates(
                        new HashSet<CurrencyPair>()
                        {
                            new CurrencyPair(pmi.CryptoCode, requestParams.CreateInvoice.Currency)
                        }, rr, CancellationToken.None).First();
                    var rateResult = await rate.Value;
                    cAmount = LightMoney.FromUnit(rateResult.BidAsk.Bid, LightMoneyUnit.BTC);
                    requestParams.CreateInvoice.Checkout.ExplicitRateScript =
                        $"{pmi.CryptoCode}_{requestParams.CreateInvoice.Currency}={rateResult.BidAsk.Bid}";
                }
                else
                {
                    cAmount = LightMoney.FromUnit(requestParams.CreateInvoice.Amount.Value!, LightMoneyUnit.BTC);
                    
                }
                requestParams.LNURLRequest.MinSendable = cAmount;
                
                if (!requestParams.AllowOverpay)
                    requestParams.LNURLRequest.MaxSendable = requestParams.LNURLRequest.MinSendable;
            }


            requestParams.LNURLRequest = await _pluginHookService.ApplyFilter("modify-lnurlp-request", requestParams.LNURLRequest) as LNURLPayRequest;
            
            var invoiceParamsCacheEntryOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(5)); // Set an appropriate expiration time

            // Store the invoice parameters in the cache
            _memoryCache.Set($"{nameof(UILNURLController)}:{k}", requestParams, invoiceParamsCacheEntryOptions);

            return Ok(requestParams.LNURLRequest);
        }
        
        
        private async Task<LNURLPayRequest> CreateLNUrlRequestFromInvoice(
            string cryptoCode,
            InvoiceEntity i,
            Data.StoreData store,
            StoreBlob blob,
            LNURLPayRequest lnurlRequest = null,
            Dictionary<string, string> lnUrlMetadata = null,
            bool allowOverpay = true)
        {
            var pmi = GetLNUrlPaymentMethodId(cryptoCode, store, out var lnUrlMethod);
            if (pmi is null)
                return null;
            lnurlRequest ??= new LNURLPayRequest();
            lnUrlMetadata ??= new Dictionary<string, string>();

            var pm = i.GetPaymentMethod(pmi);
            if (pm is null)
                return null;
            var paymentMethodDetails = (LNURLPayPaymentMethodDetails)pm.GetPaymentMethodDetails();
            bool updatePaymentMethodDetails = false;
            if (lnUrlMetadata?.TryGetValue("text/identifier", out var lnAddress) is true && lnAddress is not null)
            {
                paymentMethodDetails.ConsumedLightningAddress = lnAddress;
                updatePaymentMethodDetails = true;
            }

            if (!lnUrlMetadata.ContainsKey("text/plain"))
            {
                var invoiceDescription = blob.LightningDescriptionTemplate
                        .Replace("{StoreName}", store.StoreName ?? "", StringComparison.OrdinalIgnoreCase)
                        .Replace("{ItemDescription}", i.Metadata.ItemDesc ?? "", StringComparison.OrdinalIgnoreCase)
                        .Replace("{OrderId}", i.Metadata.OrderId ?? "", StringComparison.OrdinalIgnoreCase);
                lnUrlMetadata.Add("text/plain", invoiceDescription);
            }

            lnurlRequest.Tag = "payRequest";
            lnurlRequest.CommentAllowed = lnUrlMethod.LUD12Enabled ? 2000 : 0;
            lnurlRequest.Callback = new Uri(_linkGenerator.GetUriByAction(
                        action: nameof(GetLNURLForInvoice),
                        controller: "UILNURL",
                        values: new { pmi.CryptoCode, invoiceId = i.Id }, Request.Scheme, Request.Host, Request.PathBase));
            lnurlRequest.Metadata = JsonConvert.SerializeObject(lnUrlMetadata.Select(kv => new[] { kv.Key, kv.Value }));
            if (i.Type != InvoiceType.TopUp)
            {
                lnurlRequest.MinSendable = new LightMoney(pm.Calculate().Due.ToDecimal(MoneyUnit.Satoshi), LightMoneyUnit.Satoshi);
                if (!allowOverpay)
                    lnurlRequest.MaxSendable = lnurlRequest.MinSendable;
            }

            // We don't think BTCPay handle well 0 sats payments, just in case make it minimum one sat.
            if (lnurlRequest.MinSendable is null || lnurlRequest.MinSendable < LightMoney.Satoshis(1.0m))
                lnurlRequest.MinSendable = LightMoney.Satoshis(1.0m);

            if (lnurlRequest.MaxSendable is null)
                lnurlRequest.MaxSendable = LightMoney.FromUnit(6.12m, LightMoneyUnit.BTC);

            lnurlRequest = await _pluginHookService.ApplyFilter("modify-lnurlp-request", lnurlRequest) as LNURLPayRequest;
            if (paymentMethodDetails.PayRequest is null)
            {
                paymentMethodDetails.PayRequest = lnurlRequest;
                updatePaymentMethodDetails = true;
            }
            if (updatePaymentMethodDetails)
            {
                pm.SetPaymentMethodDetails(paymentMethodDetails);
                await _invoiceRepository.UpdateInvoicePaymentMethod(i.Id, pm);
            }
            return lnurlRequest;
        }

        PaymentMethodId GetLNUrlPaymentMethodId(string cryptoCode, Data.StoreData store, out LNURLPaySupportedPaymentMethod lnUrlSettings)
        {
            lnUrlSettings = null;
            if (!NetworkSupportsLightning(cryptoCode, out _))
                return null;
            var pmi = new PaymentMethodId(cryptoCode, PaymentTypes.LNURLPay);
            var lnpmi = new PaymentMethodId(cryptoCode, PaymentTypes.LightningLike);
            var methods = store.GetSupportedPaymentMethods(_btcPayNetworkProvider);
            var lnUrlMethod =
                methods.FirstOrDefault(method => method.PaymentId == pmi) as LNURLPaySupportedPaymentMethod;
            var lnMethod = methods.FirstOrDefault(method => method.PaymentId == lnpmi);
            if (lnUrlMethod is null || lnMethod is null)
                return null;
            var blob = store.GetStoreBlob();
            if (blob.GetExcludedPaymentMethods().Match(pmi) || blob.GetExcludedPaymentMethods().Match(lnpmi))
                return null;
            lnUrlSettings = lnUrlMethod;
            return pmi;
        }


        [HttpGet("pay/{k}")]
        public async Task<IActionResult> LNURLCallback(string k, long? amount = null, string comment = null)
        {
            if (!_memoryCache.TryGetValue<LNURLRequestParams>($"{nameof(UILNURLController)}:{k}", out var lnurlReq) || lnurlReq is null)
                return NotFound();
            if (amount is null)
            {
                return Ok(lnurlReq.LNURLRequest);
            }
            
            InvoiceEntity i;
            try
            {
                var pmi = GetLNUrlPaymentMethodId(lnurlReq.CryptoCode, lnurlReq.Store, out _);
                lnurlReq.CreateInvoice.Checkout ??= new InvoiceDataBase.CheckoutOptions();
                lnurlReq.CreateInvoice.Checkout.LazyPaymentMethods = false;
                lnurlReq.CreateInvoice.Checkout.PaymentMethods = new[] { pmi.ToStringNormalized() };
                i = await _invoiceController.CreateInvoiceCoreRaw(lnurlReq.CreateInvoice, lnurlReq.Store, Request.GetAbsoluteRoot(), lnurlReq.AdditionalTags);
                return await GetLNURLForInvoice(i, lnurlReq.CryptoCode, amount, comment);
            }
            catch (Exception e)
            {
                return this.CreateAPIError(null, e.Message);
            }
        }
        
        [HttpGet("pay/i/{invoiceId}")]
        [EnableCors(CorsPolicies.All)]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> GetLNURLForInvoice(string invoiceId, string cryptoCode,
            [FromQuery] long? amount = null, string comment = null)
        {
            
            var i = await _invoiceRepository.GetInvoice(invoiceId, true);
            return await GetLNURLForInvoice(i, cryptoCode, amount, comment);
        }
        
        [NonAction]
        private async Task<IActionResult> GetLNURLForInvoice(InvoiceEntity i, string cryptoCode,
            [FromQuery] long? amount = null, string comment = null)
        {
            if (!NetworkSupportsLightning(cryptoCode, out var network))
                return null;

            if (i is null)
                return NotFound();

            var store = await _storeRepository.FindStore(i.StoreId);
            if (store is null)
                return NotFound();

            if (i.Status == InvoiceStatusLegacy.New)
            {
                var pmi = GetLNUrlPaymentMethodId(cryptoCode, store, out var lnurlSupportedPaymentMethod);
                if (pmi is null)
                    return NotFound();

                var lightningPaymentMethod = i.GetPaymentMethod(pmi);
                var paymentMethodDetails =
                    lightningPaymentMethod?.GetPaymentMethodDetails() as LNURLPayPaymentMethodDetails;
                if (paymentMethodDetails is not null && !paymentMethodDetails.Activated)
                {
                    if (!await _invoiceActivator.ActivateInvoicePaymentMethod(pmi, i, store))
                        return NotFound();
                    i = await _invoiceRepository.GetInvoice(i.Id, true);
                    lightningPaymentMethod = i.GetPaymentMethod(pmi);
                    paymentMethodDetails = lightningPaymentMethod.GetPaymentMethodDetails() as LNURLPayPaymentMethodDetails;
                }

                if (paymentMethodDetails?.LightningSupportedPaymentMethod is null)
                    return NotFound();

                LNURLPayRequest lnurlPayRequest = paymentMethodDetails.PayRequest;
                var blob = store.GetStoreBlob();
                if (paymentMethodDetails.PayRequest is null)
                {
                    lnurlPayRequest = await CreateLNUrlRequestFromInvoice(cryptoCode, i, store, blob, allowOverpay: false);
                    if (lnurlPayRequest is null)
                        return NotFound();
                }

                if (amount is null)
                    return Ok(lnurlPayRequest);

                var amt = new LightMoney(amount.Value);
                if (amt < lnurlPayRequest.MinSendable || amount > lnurlPayRequest.MaxSendable)
                    return BadRequest(new LNUrlStatusResponse { Status = "ERROR", Reason = "Amount is out of bounds." });

                LNURLPayRequest.LNURLPayRequestCallbackResponse.ILNURLPayRequestSuccessAction successAction = null;
                if ((i.ReceiptOptions?.Enabled ?? blob.ReceiptOptions.Enabled) is true)
                {
                    successAction =
                        new LNURLPayRequest.LNURLPayRequestCallbackResponse.LNURLPayRequestSuccessActionUrl
                        {
                            Tag = "url",
                            Description = "Thank you for your purchase. Here is your receipt",
                            Url = _linkGenerator.GetUriByAction(
                                nameof(UIInvoiceController.InvoiceReceipt),
                                "UIInvoice",
                                new { i.Id },
                                Request.Scheme,
                                Request.Host,
                                Request.PathBase)
                        };
                }

                bool updatePaymentMethod = false;
                if (lnurlSupportedPaymentMethod.LUD12Enabled)
                {
                    comment = comment?.Truncate(2000);
                    if (paymentMethodDetails.ProvidedComment != comment)
                    {
                        paymentMethodDetails.ProvidedComment = comment;
                        updatePaymentMethod = true;
                    }
                }

                if (string.IsNullOrEmpty(paymentMethodDetails.BOLT11) || paymentMethodDetails.GeneratedBoltAmount != amt)
                {
                    var client =
                        _lightningLikePaymentHandler.CreateLightningClient(
                            paymentMethodDetails.LightningSupportedPaymentMethod, network);
                    if (!string.IsNullOrEmpty(paymentMethodDetails.BOLT11))
                    {
                        try
                        {
                            await client.CancelInvoice(paymentMethodDetails.InvoiceId);
                        }
                        catch (Exception)
                        {
                            //not a fully supported option
                        }
                    }

                    LightningInvoice invoice;
                    try
                    {
                        var expiry = i.ExpirationTime.ToUniversalTime() - DateTimeOffset.UtcNow;
                        var description = (await _pluginHookService.ApplyFilter("modify-lnurlp-description", lnurlPayRequest.Metadata)) as string;
                        if (description is null)
                            return NotFound();

                        var param = new CreateInvoiceParams(amt, description, expiry)
                        {
                            PrivateRouteHints = blob.LightningPrivateRouteHints,
                            DescriptionHashOnly = true
                        };
                        invoice = await client.CreateInvoice(param);
                        if (!BOLT11PaymentRequest.Parse(invoice.BOLT11, network.NBitcoinNetwork)
                                .VerifyDescriptionHash(description))
                        {
                            return BadRequest(new LNUrlStatusResponse
                            {
                                Status = "ERROR",
                                Reason = "Lightning node could not generate invoice with a valid description hash"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        return BadRequest(new LNUrlStatusResponse
                        {
                            Status = "ERROR",
                            Reason = "Lightning node could not generate invoice with description hash" + (
                                string.IsNullOrEmpty(ex.Message) ? "" : $": {ex.Message}")
                        });
                    }

                    paymentMethodDetails.BOLT11 = invoice.BOLT11;
                    paymentMethodDetails.PaymentHash = string.IsNullOrEmpty(invoice.PaymentHash) ? null : uint256.Parse(invoice.PaymentHash);
                    paymentMethodDetails.Preimage = string.IsNullOrEmpty(invoice.Preimage) ? null : uint256.Parse(invoice.Preimage);
                    paymentMethodDetails.InvoiceId = invoice.Id;
                    paymentMethodDetails.GeneratedBoltAmount = amt;
                    updatePaymentMethod = true;
                }

                if (updatePaymentMethod)
                {
                    lightningPaymentMethod.SetPaymentMethodDetails(paymentMethodDetails);
                    await _invoiceRepository.UpdateInvoicePaymentMethod(i.Id, lightningPaymentMethod);
                    _eventAggregator.Publish(new InvoiceNewPaymentDetailsEvent(i.Id, paymentMethodDetails, pmi));
                }

                return Ok(new LNURLPayRequest.LNURLPayRequestCallbackResponse
                {
                    Disposable = true,
                    Routes = Array.Empty<string>(),
                    Pr = paymentMethodDetails.BOLT11,
                    SuccessAction = successAction
                });
            }

            return BadRequest(new LNUrlStatusResponse
            {
                Status = "ERROR",
                Reason = "Invoice not in a valid payable state"
            });
        }

        private bool NetworkSupportsLightning(string cryptoCode, out BTCPayNetwork network)
        {
            network = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>(cryptoCode);
            return !(network is null || !network.SupportLightning);
        }
        
        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [HttpGet("~/stores/{storeId}/plugins/lightning-address")]
        public async Task<IActionResult> EditLightningAddress(string storeId)
        {
            if (ControllerContext.HttpContext.GetStoreData().GetEnabledPaymentIds(_btcPayNetworkProvider)
                .All(id => id.PaymentType != LNURLPayPaymentType.Instance))
            {
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Message = "LNURL is required for lightning addresses but has not yet been enabled.",
                    Severity = StatusMessageModel.StatusSeverity.Error
                });
                return RedirectToAction(nameof(UIStoresController.GeneralSettings), "UIStores", new { storeId });
            }

            var addresses =
                await _lightningAddressService.Get(new LightningAddressQuery() { StoreIds = new[] { storeId } });

            return View(new EditLightningAddressVM
            {
                Items = addresses.Select(s =>
                    {
                        var blob = s.GetBlob();
                        return new EditLightningAddressVM.EditLightningAddressItem
                        {
                            Max = blob.Max,
                            Min = blob.Min,
                            CurrencyCode = blob.CurrencyCode,
                            StoreId = storeId,
                            Username = s.Username,
                            InvoiceMetadata = blob.InvoiceMetadata?.ToString(Formatting.Indented)
                        };
                    }
                ).ToList()
            });
        }


        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [HttpPost("~/stores/{storeId}/plugins/lightning-address")]
        public async Task<IActionResult> EditLightningAddress(string storeId, [FromForm] EditLightningAddressVM vm,
            string command, [FromServices] CurrencyNameTable currencyNameTable)
        {
            if (command == "add")
            {
                if (!string.IsNullOrEmpty(vm.Add.CurrencyCode) &&
                    currencyNameTable.GetCurrencyData(vm.Add.CurrencyCode, false) is null)
                {
                    vm.AddModelError(addressVm => addressVm.Add.CurrencyCode, "Currency is invalid", this);
                }

                JObject metadata = null;
                if (!string.IsNullOrEmpty(vm.Add.InvoiceMetadata))
                {
                    try
                    {
                        metadata = JObject.Parse(vm.Add.InvoiceMetadata);
                    }
                    catch (Exception)
                    {
                        vm.AddModelError(addressVm => addressVm.Add.InvoiceMetadata, "Metadata must be a valid json object", this);
                    }
                }
                if (!ModelState.IsValid)
                {
                    return View(vm);
                }


                if (await _lightningAddressService.Set(new LightningAddressData()
                {
                    StoreDataId = storeId,
                    Username = vm.Add.Username
                }.SetBlob(new LightningAddressDataBlob()
                {
                    Max = vm.Add.Max,
                    Min = vm.Add.Min,
                    CurrencyCode = vm.Add.CurrencyCode,
                    InvoiceMetadata = metadata
                })))
                {
                    TempData.SetStatusMessageModel(new StatusMessageModel
                    {
                        Severity = StatusMessageModel.StatusSeverity.Success,
                        Message = "Lightning address added successfully."
                    });
                }
                else
                {
                    vm.AddModelError(addressVm => addressVm.Add.Username, "Username is already taken", this);

                    if (!ModelState.IsValid)
                    {
                        return View(vm);
                    }
                }
                return RedirectToAction("EditLightningAddress");
            }

            if (command.StartsWith("remove", StringComparison.InvariantCultureIgnoreCase))
            {
                var index = command.Substring(command.IndexOf(":", StringComparison.InvariantCultureIgnoreCase) + 1);
                if (await _lightningAddressService.Remove(index, storeId))
                {
                    TempData.SetStatusMessageModel(new StatusMessageModel
                    {
                        Severity = StatusMessageModel.StatusSeverity.Success,
                        Message = $"Lightning address {index} removed successfully."
                    });
                    return RedirectToAction("EditLightningAddress");
                }
                else
                {
                    vm.AddModelError(addressVm => addressVm.Add.Username, "Username could not be removed", this);

                    if (!ModelState.IsValid)
                    {
                        return View(vm);
                    }
                }
            }

            return View(vm);

        }
    }
}
