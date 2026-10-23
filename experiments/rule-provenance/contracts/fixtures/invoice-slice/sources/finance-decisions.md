# Finance decision records (synthetic fixture)

## DEC-FIN-000 Single-payment limit (superseded)
Status: superseded by DEC-FIN-001. Owner: finance-controller.
A single payment above 25,000.00 is refused.

## DEC-FIN-001 Single-payment limit
Status: accepted 2026-10-01 by finance-controller (human). Owner: finance-controller. Supersedes DEC-FIN-000.
A single payment above 10,000.00 in the invoice's currency is refused with the code PAYMENT_LIMIT_EXCEEDED.
No customer category is exempt.

## DEC-FIN-002 Payment persistence boundary
Status: accepted 2026-10-01 by security-owner (human). Owner: security-owner.
Only the payments-service role may write payments. Writes accept only validated, applied payments.
