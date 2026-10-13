namespace Funnel.Providers.Edgar;

/// <summary>
/// Statement lines built from the US-GAAP (or, for foreign filers, IFRS) XBRL tags companies file with the SEC. Companies don't
/// all use the same tag for the same thing (and switch tags over the years), so each line lists
/// its candidates in order and the first one with a value wins, period by period. Keys match
/// <c>YahooStatements</c> wherever the line means the same thing, so a sheet built from EDGAR
/// lines up row-for-row with one built from Yahoo — and the DCF reads either.
///
/// Signs follow Yahoo's convention: cash outflows (capex, buybacks, dividends) are negative.
/// </summary>
internal static class EdgarConcepts
{
    /// <param name="Tags">Candidate us-gaap tags, most specific first.</param>
    /// <param name="Negate">Flip the sign (the SEC reports payments as positive numbers).</param>
    /// <param name="Instant">A balance at a date rather than a flow over a period.</param>
    /// <param name="Unit">USD (the filing currency) | perShare | shares | pct</param>
    /// <param name="Derive">Fallback computed from other lines of the same period.</param>
    public sealed record Line(
        string Key, string Label, string Format, bool Emphasis, int Indent, string[] Tags,
        bool Negate = false, bool Instant = false, string Unit = "USD",
        Func<Func<string, double?>, double?>? Derive = null);

    private static Line H(string label) => new("#" + label, label, "header", true, 0, Array.Empty<string>());
    private static Line L(string key, string label, int indent, params string[] tags) => new(key, label, "amt", false, indent, tags);
    private static Line T(string key, string label, params string[] tags) => new(key, label, "amt", true, 0, tags);

    private static double? Sum(params double?[] v) => v.All(x => x == null) ? null : v.Sum(x => x ?? 0);
    private static double? Diff(double? a, double? b) => a == null || b == null ? null : a - b;

    public static readonly IReadOnlyList<Line> Income = new[]
    {
        L("TotalRevenue", "Total revenue", 0,
            "Revenues", "RevenueFromContractWithCustomerExcludingAssessedTax", "RevenueFromContractWithCustomerIncludingAssessedTax",
            "SalesRevenueNet", "SalesRevenueGoodsNet", "RevenuesNetOfInterestExpense"),
        L("CostOfRevenue", "Cost of revenue", 1, "CostOfRevenue", "CostOfGoodsAndServicesSold", "CostOfGoodsSold", "CostOfServices"),
        T("GrossProfit", "Gross profit", "GrossProfit") with { Derive = v => Diff(v("TotalRevenue"), v("CostOfRevenue")) },
        L("ResearchAndDevelopment", "Research & development", 1, "ResearchAndDevelopmentExpense", "ResearchAndDevelopmentExpenseExcludingAcquiredInProcessCost"),
        L("SellingGeneralAndAdministration", "Selling, general & administrative", 1, "SellingGeneralAndAdministrativeExpense"),
        L("OperatingExpense", "Total operating expenses", 0, "OperatingExpenses"),
        T("OperatingIncome", "Operating income", "OperatingIncomeLoss"),
        L("InterestIncomeNonOperating", "Interest income", 1, "InvestmentIncomeInterest", "InterestIncomeOther"),
        L("InterestExpenseNonOperating", "Interest expense", 1, "InterestExpense", "InterestExpenseNonoperating", "InterestExpenseDebt"),
        L("OtherIncomeExpense", "Other income / (expense)", 0, "NonoperatingIncomeExpense", "OtherNonoperatingIncomeExpense"),
        T("PretaxIncome", "Pre-tax income",
            "IncomeLossFromContinuingOperationsBeforeIncomeTaxesExtraordinaryItemsNoncontrollingInterest",
            "IncomeLossFromContinuingOperationsBeforeIncomeTaxesMinorityInterestAndIncomeLossFromEquityMethodInvestments"),
        L("TaxProvision", "Income tax", 1, "IncomeTaxExpenseBenefit"),
        L("NetIncomeContinuousOperations", "Net income from continuing ops", 0, "IncomeLossFromContinuingOperations"),
        L("MinorityInterests", "Minority interests", 1, "NetIncomeLossAttributableToNoncontrollingInterest") with { Negate = true },
        T("NetIncome", "Net income", "NetIncomeLoss", "ProfitLoss"),
        L("NetIncomeCommonStockholders", "Net income to common", 0, "NetIncomeLossAvailableToCommonStockholdersBasic"),
        new("BasicEPS", "EPS — basic", "ps", false, 0, new[] { "EarningsPerShareBasic" }, Unit: "perShare"),
        new("DilutedEPS", "EPS — diluted", "ps", false, 0, new[] { "EarningsPerShareDiluted" }, Unit: "perShare"),
        L("BasicAverageShares", "Weighted avg. shares — basic", 0, "WeightedAverageNumberOfSharesOutstandingBasic") with { Unit = "shares" },
        L("DilutedAverageShares", "Weighted avg. shares — diluted", 0, "WeightedAverageNumberOfDilutedSharesOutstanding") with { Unit = "shares" },
        H("Supplemental"),
        L("ReconciledDepreciation", "Depreciation & amortization", 0,
            "DepreciationDepletionAndAmortization", "DepreciationAndAmortization", "DepreciationAmortizationAndAccretionNet", "Depreciation"),
        L("EBIT", "EBIT", 0) with
        {
            Derive = v => v("PretaxIncome") is { } p && v("InterestExpenseNonOperating") is { } i ? p + i : v("OperatingIncome"),
        },
        T("EBITDA", "EBITDA") with { Derive = v => v("EBIT") is { } e && v("ReconciledDepreciation") is { } d ? e + d : null },
        new("TaxRateForCalcs", "Effective tax rate", "pct", false, 0, Array.Empty<string>(), Unit: "pct",
            Derive: v => v("TaxProvision") is { } t && v("PretaxIncome") is { } p && p != 0 ? t / p : null),
    };

    public static readonly IReadOnlyList<Line> Balance = new[]
    {
        H("Assets"),
        L("CashAndCashEquivalents", "Cash & equivalents", 1, "CashAndCashEquivalentsAtCarryingValue", "Cash",
            "CashCashEquivalentsRestrictedCashAndRestrictedCashEquivalents"),
        L("OtherShortTermInvestments", "Short-term investments", 1, "ShortTermInvestments", "MarketableSecuritiesCurrent",
            "AvailableForSaleSecuritiesDebtSecuritiesCurrent"),
        L("CashCashEquivalentsAndShortTermInvestments", "Cash & short-term investments", 0, "CashCashEquivalentsAndShortTermInvestments") with
        {
            Derive = v => Sum(v("CashAndCashEquivalents"), v("OtherShortTermInvestments")),
        },
        L("AccountsReceivable", "Accounts receivable", 1, "AccountsReceivableNetCurrent"),
        L("OtherReceivables", "Other receivables", 1, "NontradeReceivablesCurrent", "OtherReceivablesNetCurrent"),
        L("Inventory", "Inventory", 1, "InventoryNet"),
        L("PrepaidAssets", "Prepaid assets", 1, "PrepaidExpenseCurrent"),
        L("OtherCurrentAssets", "Other current assets", 1, "OtherAssetsCurrent", "PrepaidExpenseAndOtherAssetsCurrent"),
        T("CurrentAssets", "Total current assets", "AssetsCurrent"),
        L("NetPPE", "Net PP&E", 0, "PropertyPlantAndEquipmentNet",
            "PropertyPlantAndEquipmentAndFinanceLeaseRightOfUseAssetAfterAccumulatedDepreciationAndAmortization"),
        L("Goodwill", "Goodwill", 1, "Goodwill"),
        L("OtherIntangibleAssets", "Other intangibles", 1, "IntangibleAssetsNetExcludingGoodwill", "FiniteLivedIntangibleAssetsNet"),
        L("InvestmentsAndAdvances", "Long-term investments", 0, "LongTermInvestments", "MarketableSecuritiesNoncurrent",
            "AvailableForSaleSecuritiesDebtSecuritiesNoncurrent"),
        L("OtherNonCurrentAssets", "Other non-current assets", 0, "OtherAssetsNoncurrent"),
        T("TotalNonCurrentAssets", "Total non-current assets", "AssetsNoncurrent") with { Derive = v => Diff(v("TotalAssets"), v("CurrentAssets")) },
        T("TotalAssets", "Total assets", "Assets"),
        H("Liabilities"),
        L("AccountsPayable", "Accounts payable", 1, "AccountsPayableCurrent"),
        L("CurrentAccruedExpenses", "Accrued expenses", 1, "AccruedLiabilitiesCurrent"),
        L("CurrentDebt", "Short-term debt", 1, "DebtCurrent", "LongTermDebtCurrent", "ShortTermBorrowings", "CommercialPaper"),
        L("CurrentDeferredRevenue", "Deferred revenue (current)", 1, "ContractWithCustomerLiabilityCurrent", "DeferredRevenueCurrent"),
        L("OtherCurrentLiabilities", "Other current liabilities", 1, "OtherLiabilitiesCurrent"),
        T("CurrentLiabilities", "Total current liabilities", "LiabilitiesCurrent"),
        L("LongTermDebt", "Long-term debt", 1, "LongTermDebtNoncurrent", "LongTermDebtAndCapitalLeaseObligations"),
        L("LongTermCapitalLeaseObligation", "Long-term lease obligations", 1, "OperatingLeaseLiabilityNoncurrent"),
        L("NonCurrentDeferredTaxesLiabilities", "Deferred tax liabilities", 1, "DeferredIncomeTaxLiabilitiesNet", "DeferredTaxLiabilitiesNoncurrent"),
        L("OtherNonCurrentLiabilities", "Other non-current liabilities", 1, "OtherLiabilitiesNoncurrent"),
        T("TotalNonCurrentLiabilitiesNetMinorityInterest", "Total non-current liabilities", "LiabilitiesNoncurrent") with
        {
            Derive = v => Diff(v("TotalLiabilitiesNetMinorityInterest"), v("CurrentLiabilities")),
        },
        T("TotalLiabilitiesNetMinorityInterest", "Total liabilities", "Liabilities"),
        H("Equity"),
        L("CommonStock", "Common stock", 1, "CommonStockValue", "CommonStocksIncludingAdditionalPaidInCapital"),
        L("AdditionalPaidInCapital", "Additional paid-in capital", 1, "AdditionalPaidInCapital", "AdditionalPaidInCapitalCommonStock"),
        L("RetainedEarnings", "Retained earnings", 1, "RetainedEarningsAccumulatedDeficit"),
        L("TreasuryStock", "Treasury stock", 1, "TreasuryStockValue", "TreasuryStockCommonValue") with { Negate = true },
        L("GainsLossesNotAffectingRetainedEarnings", "Accumulated other comprehensive income", 1, "AccumulatedOtherComprehensiveIncomeLossNetOfTax"),
        T("StockholdersEquity", "Shareholders' equity", "StockholdersEquity"),
        L("MinorityInterest", "Minority interest", 0, "MinorityInterest"),
        T("TotalEquityGrossMinorityInterest", "Total equity", "StockholdersEquityIncludingPortionAttributableToNoncontrollingInterest") with
        {
            Derive = v => Sum(v("StockholdersEquity"), v("MinorityInterest")),
        },
        H("Supplemental"),
        L("TotalDebt", "Total debt", 0) with { Derive = v => Sum(v("CurrentDebt"), v("LongTermDebt")) },
        L("NetDebt", "Net debt", 0) with
        {
            Derive = v => v("TotalDebt") is { } d ? d - (v("CashCashEquivalentsAndShortTermInvestments") ?? 0) : null,
        },
        L("WorkingCapital", "Working capital", 0) with { Derive = v => Diff(v("CurrentAssets"), v("CurrentLiabilities")) },
        L("OrdinarySharesNumber", "Shares outstanding", 0, "CommonStockSharesOutstanding") with { Unit = "shares" },
    }.Select(l => l.Format == "header" ? l : l with { Instant = true }).ToList();

    public static readonly IReadOnlyList<Line> CashFlow = new[]
    {
        H("Operating activities"),
        L("NetIncomeFromContinuingOperations", "Net income", 0, "ProfitLoss", "NetIncomeLoss"),
        L("DepreciationAndAmortization", "Depreciation & amortization", 1,
            "DepreciationDepletionAndAmortization", "DepreciationAndAmortization", "DepreciationAmortizationAndAccretionNet", "Depreciation"),
        L("DeferredIncomeTax", "Deferred income tax", 1, "DeferredIncomeTaxExpenseBenefit"),
        L("StockBasedCompensation", "Stock-based compensation", 1, "ShareBasedCompensation", "AllocatedShareBasedCompensationExpense"),
        L("ChangeInReceivables", "Change in receivables", 1, "IncreaseDecreaseInAccountsReceivable") with { Negate = true },
        L("ChangeInInventory", "Change in inventory", 1, "IncreaseDecreaseInInventories") with { Negate = true },
        L("ChangeInPayablesAndAccruedExpense", "Change in payables & accrued", 1, "IncreaseDecreaseInAccountsPayable"),
        T("OperatingCashFlow", "Cash from operations",
            "NetCashProvidedByUsedInOperatingActivities", "NetCashProvidedByUsedInOperatingActivitiesContinuingOperations"),
        H("Investing activities"),
        L("CapitalExpenditure", "Capital expenditure", 1, "PaymentsToAcquirePropertyPlantAndEquipment", "PaymentsToAcquireProductiveAssets") with { Negate = true },
        L("NetBusinessPurchaseAndSale", "Acquisitions, net", 1, "PaymentsToAcquireBusinessesNetOfCashAcquired") with { Negate = true },
        L("PurchaseOfInvestment", "Purchases of investments", 1, "PaymentsToAcquireAvailableForSaleSecuritiesDebt",
            "PaymentsToAcquireInvestments", "PaymentsToAcquireMarketableSecurities") with { Negate = true },
        L("SaleOfInvestment", "Sales of investments", 1, "ProceedsFromSaleMaturityAndCollectionsOfInvestments",
            "ProceedsFromMaturitiesPrepaymentsAndCallsOfAvailableForSaleSecurities", "ProceedsFromSaleAndMaturityOfMarketableSecurities"),
        T("InvestingCashFlow", "Cash from investing",
            "NetCashProvidedByUsedInInvestingActivities", "NetCashProvidedByUsedInInvestingActivitiesContinuingOperations"),
        H("Financing activities"),
        L("IssuanceOfDebt", "Debt issued", 1, "ProceedsFromIssuanceOfLongTermDebt", "ProceedsFromIssuanceOfDebt"),
        L("RepaymentOfDebt", "Debt repaid", 1, "RepaymentsOfLongTermDebt", "RepaymentsOfDebt") with { Negate = true },
        L("IssuanceOfCapitalStock", "Stock issued", 1, "ProceedsFromIssuanceOfCommonStock"),
        L("RepurchaseOfCapitalStock", "Share buybacks", 1, "PaymentsForRepurchaseOfCommonStock") with { Negate = true },
        L("CashDividendsPaid", "Dividends paid", 1, "PaymentsOfDividends", "PaymentsOfDividendsCommonStock") with { Negate = true },
        T("FinancingCashFlow", "Cash from financing",
            "NetCashProvidedByUsedInFinancingActivities", "NetCashProvidedByUsedInFinancingActivitiesContinuingOperations"),
        H("Cash position"),
        L("EffectOfExchangeRateChanges", "FX effect", 0,
            "EffectOfExchangeRateOnCashCashEquivalentsRestrictedCashAndRestrictedCashEquivalents", "EffectOfExchangeRateOnCashAndCashEquivalents"),
        T("ChangesInCash", "Net change in cash",
            "CashCashEquivalentsRestrictedCashAndRestrictedCashEquivalentsPeriodIncreaseDecreaseIncludingExchangeRateEffect",
            "CashAndCashEquivalentsPeriodIncreaseDecrease"),
        L("EndCashPosition", "Ending cash", 0, "CashCashEquivalentsRestrictedCashAndRestrictedCashEquivalents",
            "CashAndCashEquivalentsAtCarryingValue") with { Instant = true },
        H("Supplemental"),
        T("FreeCashFlow", "Free cash flow") with
        {
            Derive = v => v("OperatingCashFlow") is { } o ? o + (v("CapitalExpenditure") ?? 0) : null,
        },
        L("IncomeTaxPaidSupplementalData", "Income taxes paid", 0, "IncomeTaxesPaidNet", "IncomeTaxesPaid"),
        L("InterestPaidSupplementalData", "Interest paid", 0, "InterestPaidNet", "InterestPaid"),
    };

    /// <summary>
    /// The IFRS (ifrs-full) names for the same lines, used for foreign filers' 20-F / 40-F reports —
    /// most large Canadian companies listed in the US among them. A leading "!" means the IFRS
    /// element runs the other way from the US-GAAP one (IFRS working-capital adjustments are
    /// "decrease (increase)"), so that tag skips the line's usual sign flip.
    /// </summary>
    private static readonly Dictionary<string, string[]> Ifrs = new()
    {
        ["TotalRevenue"] = new[] { "Revenue", "RevenueFromContractsWithCustomers" },
        ["CostOfRevenue"] = new[] { "CostOfSales" },
        ["GrossProfit"] = new[] { "GrossProfit" },
        ["ResearchAndDevelopment"] = new[] { "ResearchAndDevelopmentExpense" },
        ["SellingGeneralAndAdministration"] = new[] { "SellingGeneralAndAdministrativeExpense" },
        ["OperatingIncome"] = new[] { "ProfitLossFromOperatingActivities" },
        ["InterestIncomeNonOperating"] = new[] { "FinanceIncome" },
        ["InterestExpenseNonOperating"] = new[] { "FinanceCosts" },
        ["PretaxIncome"] = new[] { "ProfitLossBeforeTax" },
        ["TaxProvision"] = new[] { "IncomeTaxExpenseContinuingOperations" },
        ["NetIncomeContinuousOperations"] = new[] { "ProfitLossFromContinuingOperations" },
        ["MinorityInterests"] = new[] { "ProfitLossAttributableToNoncontrollingInterests" },
        ["NetIncome"] = new[] { "ProfitLossAttributableToOwnersOfParent", "ProfitLoss" },
        ["BasicEPS"] = new[] { "BasicEarningsLossPerShare" },
        ["DilutedEPS"] = new[] { "DilutedEarningsLossPerShare" },
        ["BasicAverageShares"] = new[] { "WeightedAverageShares" },
        ["DilutedAverageShares"] = new[] { "AdjustedWeightedAverageShares" },
        ["ReconciledDepreciation"] = new[] { "DepreciationAndAmortisationExpense", "AdjustmentsForDepreciationAndAmortisationExpense" },

        ["CashAndCashEquivalents"] = new[] { "CashAndCashEquivalents" },
        ["AccountsReceivable"] = new[] { "TradeAndOtherCurrentReceivables", "CurrentTradeReceivables" },
        ["Inventory"] = new[] { "Inventories" },
        ["PrepaidAssets"] = new[] { "CurrentPrepayments" },
        ["CurrentAssets"] = new[] { "CurrentAssets" },
        ["NetPPE"] = new[] { "PropertyPlantAndEquipment" },
        ["Goodwill"] = new[] { "Goodwill" },
        ["OtherIntangibleAssets"] = new[] { "IntangibleAssetsOtherThanGoodwill" },
        ["InvestmentsAndAdvances"] = new[] { "InvestmentsAccountedForUsingEquityMethod" },
        ["TotalNonCurrentAssets"] = new[] { "NoncurrentAssets" },
        ["TotalAssets"] = new[] { "Assets" },
        ["AccountsPayable"] = new[] { "TradeAndOtherCurrentPayables", "CurrentTradePayables" },
        ["CurrentDebt"] = new[] { "CurrentBorrowingsAndCurrentPortionOfNoncurrentBorrowings", "ShorttermBorrowings", "CurrentPortionOfLongtermBorrowings" },
        ["CurrentDeferredRevenue"] = new[] { "CurrentContractLiabilities" },
        ["CurrentLiabilities"] = new[] { "CurrentLiabilities" },
        ["LongTermDebt"] = new[] { "NoncurrentPortionOfNoncurrentBorrowings", "LongtermBorrowings" },
        ["LongTermCapitalLeaseObligation"] = new[] { "NoncurrentLeaseLiabilities" },
        ["NonCurrentDeferredTaxesLiabilities"] = new[] { "DeferredTaxLiabilities" },
        ["TotalNonCurrentLiabilitiesNetMinorityInterest"] = new[] { "NoncurrentLiabilities" },
        ["TotalLiabilitiesNetMinorityInterest"] = new[] { "Liabilities" },
        ["CommonStock"] = new[] { "IssuedCapital" },
        ["RetainedEarnings"] = new[] { "RetainedEarnings" },
        ["TreasuryStock"] = new[] { "TreasuryShares" },
        ["GainsLossesNotAffectingRetainedEarnings"] = new[] { "AccumulatedOtherComprehensiveIncome" },
        ["StockholdersEquity"] = new[] { "EquityAttributableToOwnersOfParent" },
        ["MinorityInterest"] = new[] { "NoncontrollingInterests" },
        ["TotalEquityGrossMinorityInterest"] = new[] { "Equity" },
        ["OrdinarySharesNumber"] = new[] { "NumberOfSharesOutstanding" },

        ["NetIncomeFromContinuingOperations"] = new[] { "ProfitLoss" },
        ["DepreciationAndAmortization"] = new[] { "AdjustmentsForDepreciationAndAmortisationExpense", "DepreciationAndAmortisationExpense" },
        ["DeferredIncomeTax"] = new[] { "AdjustmentsForDeferredTaxExpense" },
        ["StockBasedCompensation"] = new[] { "AdjustmentsForSharebasedPayments" },
        ["ChangeInReceivables"] = new[] { "!AdjustmentsForDecreaseIncreaseInTradeAndOtherReceivables" },
        ["ChangeInInventory"] = new[] { "!AdjustmentsForDecreaseIncreaseInInventories" },
        ["ChangeInPayablesAndAccruedExpense"] = new[] { "AdjustmentsForIncreaseDecreaseInTradeAndOtherPayables" },
        ["OperatingCashFlow"] = new[] { "CashFlowsFromUsedInOperatingActivities" },
        ["CapitalExpenditure"] = new[] { "PurchaseOfPropertyPlantAndEquipmentClassifiedAsInvestingActivities", "PurchaseOfPropertyPlantAndEquipment" },
        ["NetBusinessPurchaseAndSale"] = new[] { "CashFlowsUsedInObtainingControlOfSubsidiariesOrOtherBusinessesClassifiedAsInvestingActivities" },
        ["InvestingCashFlow"] = new[] { "CashFlowsFromUsedInInvestingActivities" },
        ["IssuanceOfDebt"] = new[] { "ProceedsFromBorrowingsClassifiedAsFinancingActivities", "ProceedsFromNoncurrentBorrowings" },
        ["RepaymentOfDebt"] = new[] { "RepaymentsOfBorrowingsClassifiedAsFinancingActivities", "RepaymentsOfNoncurrentBorrowings" },
        ["IssuanceOfCapitalStock"] = new[] { "ProceedsFromIssuingShares" },
        ["RepurchaseOfCapitalStock"] = new[] { "PaymentsToAcquireOrRedeemEntitysShares" },
        ["CashDividendsPaid"] = new[] { "DividendsPaidClassifiedAsFinancingActivities", "DividendsPaidToEquityHoldersOfParentClassifiedAsFinancingActivities" },
        ["FinancingCashFlow"] = new[] { "CashFlowsFromUsedInFinancingActivities" },
        ["EffectOfExchangeRateChanges"] = new[] { "EffectOfExchangeRateChangesOnCashAndCashEquivalents" },
        ["ChangesInCash"] = new[] { "IncreaseDecreaseInCashAndCashEquivalents" },
        ["EndCashPosition"] = new[] { "CashAndCashEquivalents" },
        ["IncomeTaxPaidSupplementalData"] = new[] { "IncomeTaxesPaidRefundClassifiedAsOperatingActivities" },
        ["InterestPaidSupplementalData"] = new[] { "InterestPaidClassifiedAsOperatingActivities", "InterestPaidClassifiedAsFinancingActivities" },
    };

    private static readonly Dictionary<StatementKind, IReadOnlyList<Line>> IfrsLines = new[] { StatementKind.Income, StatementKind.Balance, StatementKind.CashFlow }
        .ToDictionary(k => k, k => (IReadOnlyList<Line>)For(k).Select(l => Ifrs.TryGetValue(l.Key, out var t) ? l with { Tags = t } : l with { Tags = Array.Empty<string>() }).ToList());

    /// <param name="ifrs">The company reports under IFRS rather than US GAAP.</param>
    public static IReadOnlyList<Line> For(StatementKind kind, bool ifrs = false) => ifrs ? IfrsLines[kind] : kind switch
    {
        StatementKind.Income => Income,
        StatementKind.Balance => Balance,
        _ => CashFlow,
    };
}
