# Email Service Configuration & Monthly Statements

## Overview

The system includes an automated email service for sending monthly account statements to customers. Statements are generated as professional PDF documents using QuestPDF and delivered via email on the 28th of each month at 11 PM UTC.

---

## Email Service Setup

### 1. Configure SMTP Settings

Add your SMTP configuration to `appsettings.Development.json` or via environment variables:

**Option A: appsettings.json**

```json
{
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": 587,
    "SmtpUsername": "your-email@gmail.com",
    "SmtpPassword": "your-app-password",
    "FromEmail": "noreply@nomba.local",
    "FromName": "Nomba Financial Services"
  }
}
```

**Option B: .NET User Secrets (Recommended for local development)**

```bash
dotnet user-secrets set "Email:SmtpHost" "smtp.gmail.com" --project Nomba_Hackathon.csproj
dotnet user-secrets set "Email:SmtpPort" "587" --project Nomba_Hackathon.csproj
dotnet user-secrets set "Email:SmtpUsername" "your-email@gmail.com" --project Nomba_Hackathon.csproj
dotnet user-secrets set "Email:SmtpPassword" "your-app-password" --project Nomba_Hackathon.csproj
dotnet user-secrets set "Email:FromEmail" "noreply@nomba.local" --project Nomba_Hackathon.csproj
dotnet user-secrets set "Email:FromName" "Nomba Financial Services" --project Nomba_Hackathon.csproj
```

**Option C: Environment Variables**

```bash
export Email__SmtpHost="smtp.gmail.com"
export Email__SmtpPort="587"
export Email__SmtpUsername="your-email@gmail.com"
export Email__SmtpPassword="your-app-password"
export Email__FromEmail="noreply@nomba.local"
export Email__FromName="Nomba Financial Services"
```

### 2. Gmail Setup (if using Gmail SMTP)

1. Enable 2-Factor Authentication on your Google Account
2. Generate an App Password:
   - Go to https://myaccount.google.com/apppasswords
   - Select "Mail" and "Windows Computer"
   - Copy the 16-character password
   - Use this as your `SmtpPassword`

### 3. Other Email Providers

- **SendGrid**: `smtp.sendgrid.net:587`
- **AWS SES**: `email-smtp.[region].amazonaws.com:587`
- **Mailgun**: `smtp.mailgun.org:587`
- **Postmark**: `smtp.postmarkapp.com:587`

---

## Monthly Statement Job

The system automatically sends monthly account statements via Hangfire on a recurring schedule:

**Schedule**: 28th of each month at 23:00 UTC (11 PM)

**Cron Expression**: `0 23 28 * *`

**What it does**:
1. Queries all ACTIVE customers in the database
2. Generates a PDF statement for each customer with:
   - Customer name and email
   - List of all virtual accounts and balances
   - Transaction history (last 20 transactions)
   - Account summary with NUBAN and status
3. Sends an email with the PDF attached

---

## Statement Content

The generated PDF includes:

- **Header**: Statement title, customer name, email, generation timestamp
- **Account Summary**: Table with account names, NUBANs, status, and balances
- **Transaction History**: Recent transactions with reference, date, status, and amount
- **Footer**: Page numbers
- **Formatting**: Professional layout with colors, proper spacing, and typography

---

## Monitoring & Troubleshooting

### Check Hangfire Dashboard

Visit `http://localhost:5000/hangfire` to monitor job execution:

- **Succeeded**: Successfully sent statements
- **Failed**: Review error logs for SMTP or database issues
- **Recurring**: "monthly-statements" job shows next scheduled run

### Debug Email Issues

Enable logging in `appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Nomba_Hackathon.Service.EmailService": "Debug",
      "Nomba_Hackathon.Service.MonthlyStatementService": "Debug"
    }
  }
}
```

### Common Issues

| Issue | Cause | Solution |
|-------|-------|----------|
| "Email service not configured" | SmtpHost is empty | Set all Email:* config values |
| "Failed to send email" | Invalid SMTP credentials | Verify username/password |
| Port 587 timeout | Firewall blocking SMTP | Use port 25 or 465, or check firewall |
| Job not running | Hangfire server disabled | Ensure `Hangfire:DisableServer != "true"` |
| Wrong sender | FromEmail not set | Configure Email:FromEmail and Email:FromName |

---

## API Endpoints for On-Demand Statements

You can manually generate and download statements in PDF format:

**Customer Statement PDF**:
```bash
GET /customers/{customerId}/statement/pdf?from=2025-01-01&to=2025-01-31
```

**Account Statement PDF**:
```bash
GET /account/{accountRef}/statement/pdf?from=2025-01-01&to=2025-01-31
```

Both endpoints return a downloadable PDF file.

---

## Email Template Customization

To customize the email body, edit the `MonthlyStatementService.SendMonthlyStatementsAsync()` method:

```csharp
var body = $@"
Dear {customer.Name},

<Your custom message here>

Best regards,
Nomba Financial Services
";
```

---

## Disabling Monthly Statements

To disable automatic monthly statement sending, comment out the recurring job in `Program.cs`:

```csharp
// Temporarily disabled
// RecurringJob.AddOrUpdate<MonthlyStatementService>(
//     "monthly-statements", svc => svc.SendMonthlyStatementsAsync(), "0 23 28 * *");
```

Restart the application for changes to take effect.
