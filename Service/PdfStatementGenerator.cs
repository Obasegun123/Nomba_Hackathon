using Microsoft.EntityFrameworkCore;
using Nomba_Hackathon.Data;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Nomba_Hackathon.Service;

public class PdfStatementGenerator
{
    private readonly LedgerDbContext _db;
    private readonly LedgerQueryService _queries;
    private readonly ILogger<PdfStatementGenerator> _logger;

    public PdfStatementGenerator(LedgerDbContext db, LedgerQueryService queries, ILogger<PdfStatementGenerator> logger)
    {
        _db = db;
        _queries = queries;
        _logger = logger;
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<byte[]> GenerateCustomerStatementAsync(string customerId, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var customer = await _db.Customers.FindAsync(customerId);
        if (customer is null)
            throw new KeyNotFoundException($"Customer {customerId} not found");

        var accounts = await _db.VirtualAccounts
            .Where(a => a.CustomerId == customerId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();

        var accountDetails = await Task.WhenAll(accounts.Select(async a =>
            new
            {
                Account = a,
                Balance = await _queries.GetBalanceAsync(a.AccountRef)
            }));

        var transactions = await _queries.GetTransactionsAsync(null, null, 1, 1000);

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(20);

                page.Header().Element(header =>
                {
                    header.Column(col =>
                    {
                        col.Item().Text("ACCOUNT STATEMENT").FontSize(20).Bold();
                        col.Item().Text($"Customer: {customer.Name}").FontSize(12);
                        col.Item().Text($"Email: {customer.Email}").FontSize(10).FontColor(Colors.Grey.Darken2);
                        col.Item().Text($"Generated: {DateTimeOffset.UtcNow:dd MMM yyyy HH:mm:ss UTC}").FontSize(10).FontColor(Colors.Grey.Darken2);
                    });
                });

                page.Content().Column(col =>
                {
                    col.Spacing(12);

                    col.Item().Element(summary =>
                    {
                        summary.Column(c =>
                        {
                            c.Item().Text("Account Summary").FontSize(14).Bold();
                            c.Item().PaddingTop(8).Table(table =>
                            {
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.RelativeColumn();
                                    columns.RelativeColumn();
                                    columns.RelativeColumn();
                                    columns.RelativeColumn();
                                });

                                table.Header(header =>
                                {
                                    header.Cell().Element(CellStyle).Text("Account Name");
                                    header.Cell().Element(CellStyle).Text("NUBAN");
                                    header.Cell().Element(CellStyle).Text("Status");
                                    header.Cell().Element(CellStyle).AlignRight().Text("Balance");
                                });

                                foreach (var acct in accountDetails)
                                {
                                    table.Cell().Element(CellStyle).Text(acct.Account.AccountName);
                                    table.Cell().Element(CellStyle).Text(acct.Account.Nuban ?? "N/A");
                                    table.Cell().Element(CellStyle).Text(acct.Account.Status);
                                    table.Cell().Element(CellStyle).AlignRight().Text($"₦{acct.Balance?.Balance:N2}");
                                }
                            });
                        });
                    });

                    col.Item().PaddingTop(12).Element(txns =>
                    {
                        txns.Column(c =>
                        {
                            c.Item().Text("Recent Transactions").FontSize(14).Bold();
                            c.Item().PaddingTop(8).Table(table =>
                            {
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.RelativeColumn(1);
                                    columns.RelativeColumn(1);
                                    columns.RelativeColumn(1);
                                    columns.RelativeColumn(1);
                                });

                                table.Header(header =>
                                {
                                    header.Cell().Element(CellStyle).Text("Reference");
                                    header.Cell().Element(CellStyle).Text("Date");
                                    header.Cell().Element(CellStyle).Text("Status");
                                    header.Cell().Element(CellStyle).AlignRight().Text("Amount");
                                });

                                var recentTx = transactions.Items.Take(20);
                                foreach (var tx in recentTx)
                                {
                                    table.Cell().Element(CellStyle).Text(tx.ReferenceCode ?? "N/A");
                                    table.Cell().Element(CellStyle).Text(tx.CreatedAt.ToString("dd MMM yyyy"));
                                    table.Cell().Element(CellStyle).Text(tx.Status);
                                    table.Cell().Element(CellStyle).AlignRight().Text($"₦{tx.Amount:N2}");
                                }

                                if (!recentTx.Any())
                                {
                                    table.Cell().ColumnSpan(4).Padding(8).Text("No transactions found");
                                }
                            });
                        });
                    });
                });

                page.Footer().AlignCenter().Text(txt =>
                {
                    txt.Span("Page ");
                    txt.CurrentPageNumber();
                    txt.Span(" of ");
                    txt.TotalPages();
                });
            });
        });

        return doc.GeneratePdf();
    }

    public async Task<byte[]> GenerateAccountStatementAsync(string accountRef, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var account = await _db.VirtualAccounts.FindAsync(accountRef);
        if (account is null)
            throw new KeyNotFoundException($"Account {accountRef} not found");

        var balance = await _queries.GetBalanceAsync(accountRef);
        var transactions = await _queries.GetTransactionsAsync(null, accountRef, 1, 1000);

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(20);

                page.Header().Element(header =>
                {
                    header.Column(col =>
                    {
                        col.Item().Text("VIRTUAL ACCOUNT STATEMENT").FontSize(20).Bold();
                        col.Item().Text($"Account: {account.AccountName}").FontSize(12);
                        col.Item().Text($"NUBAN: {account.Nuban ?? "N/A"}").FontSize(10);
                        col.Item().Text($"Status: {account.Status}").FontSize(10);
                        col.Item().Text($"Generated: {DateTimeOffset.UtcNow:dd MMM yyyy HH:mm:ss UTC}").FontSize(10).FontColor(Colors.Grey.Darken2);
                    });
                });

                page.Content().Column(col =>
                {
                    col.Spacing(12);

                    col.Item().Element(bal =>
                    {
                        bal.Column(c =>
                        {
                            c.Item().Text("Balance Information").FontSize(14).Bold();
                            c.Item().PaddingTop(8).Row(row =>
                            {
                                row.RelativeColumn().Column(r =>
                                {
                                    r.Item().Text("Current Balance").FontSize(10).FontColor(Colors.Grey.Medium);
                                    r.Item().PaddingTop(4).Text($"₦{balance?.Balance:N2}").FontSize(18).Bold();
                                });
                            });
                        });
                    });

                    col.Item().PaddingTop(12).Element(txns =>
                    {
                        txns.Column(c =>
                        {
                            c.Item().Text("Transaction History").FontSize(14).Bold();
                            c.Item().PaddingTop(8).Table(table =>
                            {
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.RelativeColumn(1);
                                    columns.RelativeColumn(1);
                                    columns.RelativeColumn(1);
                                    columns.RelativeColumn(1);
                                });

                                table.Header(header =>
                                {
                                    header.Cell().Element(CellStyle).Text("Reference");
                                    header.Cell().Element(CellStyle).Text("Date");
                                    header.Cell().Element(CellStyle).Text("Status");
                                    header.Cell().Element(CellStyle).AlignRight().Text("Amount");
                                });

                                foreach (var tx in transactions.Items.Take(30))
                                {
                                    table.Cell().Element(CellStyle).Text(tx.ReferenceCode ?? "N/A");
                                    table.Cell().Element(CellStyle).Text(tx.CreatedAt.ToString("dd MMM yyyy"));
                                    table.Cell().Element(CellStyle).Text(tx.Status);
                                    table.Cell().Element(CellStyle).AlignRight().Text($"₦{tx.Amount:N2}");
                                }

                                if (!transactions.Items.Any())
                                {
                                    table.Cell().ColumnSpan(4).Padding(8).Text("No transactions found");
                                }
                            });
                        });
                    });
                });

                page.Footer().AlignCenter().Text(txt =>
                {
                    txt.Span("Page ");
                    txt.CurrentPageNumber();
                    txt.Span(" of ");
                    txt.TotalPages();
                });
            });
        });

        return doc.GeneratePdf();
    }

    private static IContainer CellStyle(IContainer container)
    {
        return container.Padding(8).Background(Colors.Blue.Lighten5);
    }
}
