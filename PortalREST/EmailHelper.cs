using ApiHelpers;
using Serilog;
using System.Net;
using System.Net.Mail;
using System.Text;
namespace PortalREST;


public static class EmailHelper
{
    public static string BuildEmailContent(List<Place> places, string notes)
    {
        StringBuilder emailBody = new StringBuilder();
        emailBody.Append(@"
    <html>
    <head>
        <style>
            body { font-family: Arial, sans-serif; color: #333; }
            h2 { color: #007bff; }
            ul { list-style-type: none; padding: 0; }
            li { margin-bottom: 20px; }
            table { width: 100%; border-collapse: collapse; margin-top: 10px; }
            th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }
            th { background-color: #f4f4f4; }
            hr { border: 0; height: 1px; background: #ddd; margin: 20px 0; }
        </style>
    </head>
    <body>
    ");

        emailBody.Append("<h2>Slow Dialing - Your Saved Places</h2>");

        if (!string.IsNullOrWhiteSpace(notes))
        {
            emailBody.AppendFormat("<p><b>Operator Notes:</b> {0}</p>", WebUtility.HtmlEncode(notes));
        }

        foreach (var place in places)
        {
            emailBody.AppendFormat("<h3>{0}</h3>", WebUtility.HtmlEncode(place.DisplayName?.Text));
            emailBody.AppendFormat("<p><b>Address:</b> {0}</p>", WebUtility.HtmlEncode(place.FormattedAddress));

            if (!string.IsNullOrWhiteSpace(place.PrimaryType))
                emailBody.AppendFormat("<p><b>Primary Type:</b> {0}</p>", WebUtility.HtmlEncode(place.PrimaryType));

            if (place.Types != null && place.Types.Any())
                emailBody.AppendFormat("<p><b>Categories:</b> {0}</p>", string.Join(", ", place.Types.Select(WebUtility.HtmlEncode)));

            if (!string.IsNullOrWhiteSpace(place.BusinessStatus))
                emailBody.AppendFormat("<p><b>Business Status:</b> {0}</p>", WebUtility.HtmlEncode(place.BusinessStatus));

            if (!string.IsNullOrWhiteSpace(place.WebsiteUri))
                emailBody.AppendFormat("<p><b>Website:</b> <a href='{0}' target='_blank'>{0}</a></p>", place.WebsiteUri);

            if (!string.IsNullOrWhiteSpace(place.GoogleMapsUri))
                emailBody.AppendFormat("<p><b>Google Maps:</b> <a href='{0}' target='_blank'>View Location</a></p>", place.GoogleMapsUri);

            // Display boolean attributes
            AppendBooleanInfo(emailBody, "Curbside Pickup", place.CurbsidePickup);
            AppendBooleanInfo(emailBody, "Reservable", place.Reservable);
            AppendBooleanInfo(emailBody, "Serves Beer", place.ServesBeer);
            AppendBooleanInfo(emailBody, "Serves Wine", place.ServesWine);
            AppendBooleanInfo(emailBody, "Serves Vegetarian Food", place.ServesVegetarianFood);
            AppendBooleanInfo(emailBody, "Outdoor Seating", place.OutdoorSeating);
            AppendBooleanInfo(emailBody, "Live Music", place.LiveMusic);
            AppendBooleanInfo(emailBody, "Good for Watching Sports", place.GoodForWatchingSports);
            AppendBooleanInfo(emailBody, "Good for Groups", place.GoodForGroups);
            AppendBooleanInfo(emailBody, "Good for Children", place.GoodForChildren);
            AppendBooleanInfo(emailBody, "Serves Cocktails", place.ServesCocktails);
            AppendBooleanInfo(emailBody, "Serves Coffee", place.ServesCoffee);
            AppendBooleanInfo(emailBody, "Allows Dogs", place.AllowsDogs);
            AppendBooleanInfo(emailBody, "Has Restroom", place.Restroom);

            // Display Accessibility Information
            if (place.AccessibilityOptions != null)
            {
                emailBody.Append("<h4>Accessibility Options</h4>");
                AppendBooleanInfo(emailBody, "Wheelchair Accessible Parking", place.AccessibilityOptions.WheelchairAccessibleParking);
                AppendBooleanInfo(emailBody, "Wheelchair Accessible Entrance", place.AccessibilityOptions.WheelchairAccessibleEntrance);
                AppendBooleanInfo(emailBody, "Wheelchair Accessible Restroom", place.AccessibilityOptions.WheelchairAccessibleRestroom);
                AppendBooleanInfo(emailBody, "Wheelchair Accessible Seating", place.AccessibilityOptions.WheelchairAccessibleSeating);
            }

            // Improved opening hours display
            if (place.CurrentOpeningHours != null && place.CurrentOpeningHours.Periods != null)
            {
                emailBody.Append("<h4>Operating Hours</h4>");
                emailBody.Append("<table><tr><th>Day</th><th>Open</th><th>Close</th></tr>");

                foreach (var period in place.CurrentOpeningHours.Periods)
                {
                    string day = GetDayName(period.Open?.Day);
                    string openTime = period.Open != null ? $"{period.Open.Hour:D2}:{period.Open.Minute:D2}" : "N/A";
                    string closeTime = period.Close != null ? $"{period.Close.Hour:D2}:{period.Close.Minute:D2}" : "N/A";

                    emailBody.AppendFormat("<tr><td>{0}</td><td>{1}</td><td>{2}</td></tr>", day, openTime, closeTime);
                }

                emailBody.Append("</table>");
            }

            if (place.PriceLevel != null)
                emailBody.AppendFormat("<p><b>Price Level:</b> {0}</p>", WebUtility.HtmlEncode(place.PriceLevel));

            if (!string.IsNullOrWhiteSpace(place.EditorialSummary?.Text))
                emailBody.AppendFormat("<p><b>Summary:</b> {0}</p>", WebUtility.HtmlEncode(place.EditorialSummary.Text));

            emailBody.Append("<hr>");
        }

        emailBody.Append("</body></html>");
        return emailBody.ToString();
    }

    /// <summary>
    /// Appends boolean flag information in a formatted way.
    /// </summary>
    private static void AppendBooleanInfo(StringBuilder emailBody, string label, bool? value)
    {
        if (value.HasValue)
        {
            emailBody.AppendFormat("<p><b>{0}:</b> {1}</p>", WebUtility.HtmlEncode(label), value.Value ? "Yes" : "No");
        }
    }

    /// <summary>
    /// Converts Google API day number to human-readable day name.
    /// </summary>
    private static string GetDayName(int? googleDay)
    {
        return googleDay switch
        {
            0 => "Sunday",
            1 => "Monday",
            2 => "Tuesday",
            3 => "Wednesday",
            4 => "Thursday",
            5 => "Friday",
            6 => "Saturday",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Sends the email using SMTP.
    /// </summary>
    public static async Task<bool> SendEmail(string emailServer, string emailAddress, string emailPass, string recipientEmail, string emailBody)
    {
        try
        {
            using var smtpClient = new SmtpClient(emailServer)
            {
                Port = 587,
                Credentials = new NetworkCredential(emailAddress, emailPass),
                EnableSsl = true,
            };

            using var mailMessage = new MailMessage
            {
                From = new MailAddress(emailAddress),
                Subject = "Slow Dialing - Your Saved Places",
                Body = emailBody,
                IsBodyHtml = true,
            };

            mailMessage.To.Add(recipientEmail);
            mailMessage.To.Add(emailAddress);

            await smtpClient.SendMailAsync(mailMessage);
            Log.Information($"Email successfully sent to {recipientEmail}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[EMAIL ERROR] Failed to send email: {ex.Message}");
            return false;
        }
    }
}
