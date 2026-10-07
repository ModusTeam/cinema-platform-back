using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Cinema.Infrastructure.Services;

public class QuestPdfTicketGenerator : ITicketGenerator
{
    public QuestPdfTicketGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] GenerateTicketPdf(TicketPurchasedMessage data)
    {
        byte[] qrCodeImage = GenerateQrCode(data.OrderId.ToString());
        
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A5.Landscape());
                page.Margin(1, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(12).FontFamily(Fonts.Arial));

                page.Header()
                    .Text("CINEMA TICKET")
                    .SemiBold().FontSize(24).FontColor(Colors.Blue.Medium);

                page.Content()
                    .PaddingVertical(1, Unit.Centimetre)
                    .Row(row =>
                    {
                        row.RelativeItem(2).Column(col => 
                        {
                            col.Item().Text(data.MovieTitle).FontSize(20).Bold();
                            col.Item().Text($"Date: {data.SessionDate:dd MMMM yyyy, HH:mm}").FontSize(14);
                            col.Item().Text($"Guest: {data.UserName}").FontSize(14);
                            col.Item().PaddingTop(10).Text($"Order ID: {data.OrderId}").FontSize(10).FontColor(Colors.Grey.Medium);
                        });
                        
                        row.RelativeItem(1).AlignRight().AlignMiddle().Column(col =>
                        {
                            col.Item().Image(qrCodeImage).FitArea();
                            col.Item().AlignCenter().Text("Scan at entrance").FontSize(8);
                        });
                    });
                
                page.Footer()
                    .AlignCenter()
                    .Text(x =>
                    {
                        x.Span("Cinema Platform Inc. ");
                        x.CurrentPageNumber();
                    });
            });
        });

        return document.GeneratePdf();
    }

    private byte[] GenerateQrCode(string content)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(qrCodeData);
        return qrCode.GetGraphic(20);
    }
}
