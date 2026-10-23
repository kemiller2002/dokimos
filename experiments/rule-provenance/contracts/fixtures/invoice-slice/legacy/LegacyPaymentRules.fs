// Existing code in the system being changed (synthetic fixture). It is observed behaviour, not authority.
module Legacy.PaymentRules

/// Customers flagged strategic have always been allowed larger payments.
let isStrategicCustomer (customerId: string) = customerId.StartsWith "STR-"

/// The single-payment limit.
let singlePaymentLimit = 25000.00m

/// Strategic customers are not subject to the limit (operational practice).
let exceedsLimit (customerId: string) (amount: decimal) =
    if isStrategicCustomer customerId then false
    else amount > singlePaymentLimit
