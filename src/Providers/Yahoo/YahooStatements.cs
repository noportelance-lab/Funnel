namespace Funnel.Providers.Yahoo;

/// <summary>
/// The line items of each statement, in presentation order, keyed by Yahoo's
/// fundamentals-timeseries names (the endpoint wants them prefixed: annualTotalRevenue,
/// quarterlyTotalRevenue, trailingTotalRevenue). Rows Yahoo has no data for are dropped, so a
/// bank and a software company both come out clean from the same list.
///
/// Format: amt | ps (per share) | pct. A key starting with "#" is a section header.
/// </summary>
internal static class YahooStatements
{
    public sealed record Item(string Key, string Label, string Format = "amt", bool Emphasis = false, int Indent = 0);

    private static Item H(string label) => new("#" + label, label, "header");
    private static Item T(string key, string label) => new(key, label, "amt", true);
    private static Item L(string key, string label, int indent = 0) => new(key, label, "amt", false, indent);

    public static readonly IReadOnlyList<Item> Income = new[]
    {
        L("TotalRevenue", "Total revenue"),
        L("CostOfRevenue", "Cost of revenue", 1),
        T("GrossProfit", "Gross profit"),
        L("ResearchAndDevelopment", "Research & development", 1),
        L("SellingGeneralAndAdministration", "Selling, general & administrative", 1),
        L("OtherOperatingExpenses", "Other operating expenses", 1),
        L("OperatingExpense", "Total operating expenses"),
        T("OperatingIncome", "Operating income"),
        L("InterestIncomeNonOperating", "Interest income", 1),
        L("InterestExpenseNonOperating", "Interest expense", 1),
        L("NetNonOperatingInterestIncomeExpense", "Net interest income / (expense)"),
        L("OtherIncomeExpense", "Other income / (expense)"),
        T("PretaxIncome", "Pre-tax income"),
        L("TaxProvision", "Income tax", 1),
        L("NetIncomeContinuousOperations", "Net income from continuing ops"),
        L("MinorityInterests", "Minority interests", 1),
        T("NetIncome", "Net income"),
        L("NetIncomeCommonStockholders", "Net income to common"),
        new("BasicEPS", "EPS — basic", "ps"),
        new("DilutedEPS", "EPS — diluted", "ps"),
        L("BasicAverageShares", "Weighted avg. shares — basic"),
        L("DilutedAverageShares", "Weighted avg. shares — diluted"),
        H("Supplemental"),
        L("EBIT", "EBIT"),
        T("EBITDA", "EBITDA"),
        L("NormalizedEBITDA", "Normalized EBITDA"),
        L("ReconciledDepreciation", "Depreciation & amortization"),
        L("TotalUnusualItems", "Unusual items"),
        new("TaxRateForCalcs", "Effective tax rate", "pct"),
    };

    public static readonly IReadOnlyList<Item> Balance = new[]
    {
        H("Assets"),
        L("CashAndCashEquivalents", "Cash & equivalents", 1),
        L("OtherShortTermInvestments", "Short-term investments", 1),
        L("CashCashEquivalentsAndShortTermInvestments", "Cash & short-term investments"),
        L("AccountsReceivable", "Accounts receivable", 1),
        L("OtherReceivables", "Other receivables", 1),
        L("Receivables", "Total receivables"),
        L("Inventory", "Inventory", 1),
        L("PrepaidAssets", "Prepaid assets", 1),
        L("OtherCurrentAssets", "Other current assets", 1),
        T("CurrentAssets", "Total current assets"),
        L("GrossPPE", "Gross PP&E", 1),
        L("AccumulatedDepreciation", "Accumulated depreciation", 1),
        L("NetPPE", "Net PP&E"),
        L("Goodwill", "Goodwill", 1),
        L("OtherIntangibleAssets", "Other intangibles", 1),
        L("GoodwillAndOtherIntangibleAssets", "Goodwill & intangibles"),
        L("InvestmentsAndAdvances", "Long-term investments"),
        L("NonCurrentDeferredTaxesAssets", "Deferred tax assets"),
        L("OtherNonCurrentAssets", "Other non-current assets"),
        T("TotalNonCurrentAssets", "Total non-current assets"),
        T("TotalAssets", "Total assets"),
        H("Liabilities"),
        L("AccountsPayable", "Accounts payable", 1),
        L("CurrentAccruedExpenses", "Accrued expenses", 1),
        L("CurrentDebt", "Short-term debt", 1),
        L("CurrentCapitalLeaseObligation", "Current lease obligations", 1),
        L("CurrentDeferredRevenue", "Deferred revenue (current)", 1),
        L("OtherCurrentLiabilities", "Other current liabilities", 1),
        T("CurrentLiabilities", "Total current liabilities"),
        L("LongTermDebt", "Long-term debt", 1),
        L("LongTermCapitalLeaseObligation", "Long-term lease obligations", 1),
        L("NonCurrentDeferredRevenue", "Deferred revenue (non-current)", 1),
        L("NonCurrentDeferredTaxesLiabilities", "Deferred tax liabilities", 1),
        L("OtherNonCurrentLiabilities", "Other non-current liabilities", 1),
        T("TotalNonCurrentLiabilitiesNetMinorityInterest", "Total non-current liabilities"),
        T("TotalLiabilitiesNetMinorityInterest", "Total liabilities"),
        H("Equity"),
        L("CommonStock", "Common stock", 1),
        L("AdditionalPaidInCapital", "Additional paid-in capital", 1),
        L("RetainedEarnings", "Retained earnings", 1),
        L("TreasuryStock", "Treasury stock", 1),
        L("GainsLossesNotAffectingRetainedEarnings", "Accumulated other comprehensive income", 1),
        T("StockholdersEquity", "Shareholders' equity"),
        L("MinorityInterest", "Minority interest"),
        T("TotalEquityGrossMinorityInterest", "Total equity"),
        H("Supplemental"),
        L("TotalDebt", "Total debt"),
        L("NetDebt", "Net debt"),
        L("WorkingCapital", "Working capital"),
        L("InvestedCapital", "Invested capital"),
        L("TangibleBookValue", "Tangible book value"),
        L("OrdinarySharesNumber", "Shares outstanding"),
    };

    public static readonly IReadOnlyList<Item> CashFlow = new[]
    {
        H("Operating activities"),
        L("NetIncomeFromContinuingOperations", "Net income"),
        L("DepreciationAndAmortization", "Depreciation & amortization", 1),
        L("DeferredIncomeTax", "Deferred income tax", 1),
        L("StockBasedCompensation", "Stock-based compensation", 1),
        L("OtherNonCashItems", "Other non-cash items", 1),
        L("ChangeInReceivables", "Change in receivables", 1),
        L("ChangeInInventory", "Change in inventory", 1),
        L("ChangeInPayablesAndAccruedExpense", "Change in payables & accrued", 1),
        L("ChangeInWorkingCapital", "Change in working capital"),
        T("OperatingCashFlow", "Cash from operations"),
        H("Investing activities"),
        L("CapitalExpenditure", "Capital expenditure", 1),
        L("NetBusinessPurchaseAndSale", "Acquisitions, net", 1),
        L("PurchaseOfInvestment", "Purchases of investments", 1),
        L("SaleOfInvestment", "Sales of investments", 1),
        L("NetOtherInvestingChanges", "Other investing", 1),
        T("InvestingCashFlow", "Cash from investing"),
        H("Financing activities"),
        L("IssuanceOfDebt", "Debt issued", 1),
        L("RepaymentOfDebt", "Debt repaid", 1),
        L("IssuanceOfCapitalStock", "Stock issued", 1),
        L("RepurchaseOfCapitalStock", "Share buybacks", 1),
        L("CashDividendsPaid", "Dividends paid", 1),
        L("NetOtherFinancingCharges", "Other financing", 1),
        T("FinancingCashFlow", "Cash from financing"),
        H("Cash position"),
        L("EffectOfExchangeRateChanges", "FX effect"),
        T("ChangesInCash", "Net change in cash"),
        L("BeginningCashPosition", "Beginning cash"),
        L("EndCashPosition", "Ending cash"),
        H("Supplemental"),
        T("FreeCashFlow", "Free cash flow"),
        L("IncomeTaxPaidSupplementalData", "Income taxes paid"),
        L("InterestPaidSupplementalData", "Interest paid"),
    };

    public static IReadOnlyList<Item> For(StatementKind kind) => kind switch
    {
        StatementKind.Income => Income,
        StatementKind.Balance => Balance,
        _ => CashFlow,
    };

    public static string Title(StatementKind kind) => kind switch
    {
        StatementKind.Income => "Income Statement",
        StatementKind.Balance => "Balance Sheet",
        _ => "Cash Flow Statement",
    };
}
