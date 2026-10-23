# Invoicing requirements (synthetic fixture)

Synthetic test input for the verified-construction vertical slice. Not the requirements of any product.

## REQ-INV-001 Invoice lifecycle
An invoice is created as a draft, is issued to the customer, and is paid. A draft or an issued invoice that has
received no payment may be voided. A paid invoice and a voided invoice accept no further commands.

## REQ-INV-002 Payment validation
A payment is a positive amount in the invoice's currency. A payment may be partial. A payment never exceeds the
invoice's outstanding balance. An invoice whose outstanding balance reaches zero is paid.

## REQ-INV-003 Payment persistence
A payment is persisted only after it has been validated and applied, and only by the payments service.
