/* Copyright (c) 2010 Derrick Coetzee

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE. */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using DotNetWikiBot;

namespace PicasaReview
{
    class Program
    {
        static string wikiSiteUrl = "http://commons.wikimedia.org";
        static string username;
        static string password;

        //static string catScanning = null;
        static string catScanning = "Category:Picasa_Web_Albums_review_needed";
        // Use this one to go back over the human review cat looking for any we missed, useful
        // during development.
        //static string catScanning = "Category:Picasa Web Albums files needing human review";
        //static string catScanning = "Category:Picasa Web Albums files not requiring review";

        static string searchScanning = null;
        //static string searchScanning = "picasaweb";
        // For debugging, resume after the noted file, or null to start at the beginning
        //static string resumeAfter = "File:Sa-sieng-se 2.jpg";
        static string resumeAfter = null;

        static void Main(string[] args)
        {
            username = args[0];
            password = args[1];

            Bot.unsafeHttpHeaderParsingUsed = 0;
            Site site = new Site(wikiSiteUrl, username, password);
            while (true)
            {
                int totalPagesProcessed = 0;
                try
                {
                    PageList pageList = new PageList(site);
#if true
                    if (catScanning != null)
                        pageList.FillFromCategory(catScanning);
                    else
                        pageList.FillFromSearchResults(searchScanning, int.MaxValue);
#endif

                    string failureReason = null;
                    foreach (Page page in pageList)
                    {
                        if (resumeAfter != null)
                        {
                            if (page.title == resumeAfter) resumeAfter = null;
                            continue;
                        }

                        totalPagesProcessed++;
                        if (!page.title.StartsWith("File:"))
                        {
                            continue;
                        }
                        while (true)
                        {
                            try
                            {
                                page.Load();

                                if (tagCompletedRegex.Match(page.text).Success)
                                {
                                    break;
                                }

                                if (!tagRegex.Match(page.text).Success &&
                                    !page.text.ToLower().Contains("{{picasareviewunnecessary}}") &&
                                    !page.text.ToLower().Contains("user:picasa review bot/reviewed-error"))
                                {
                                    Regex licenseReviewRegex = new Regex("{{LicenseReview\\|[^|]*\\|([^|]*)\\|([^}]*)}}");
                                    Match m;
                                    if ((m = licenseReviewRegex.Match(page.text)).Success)
                                    {
                                        page.text = licenseReviewRegex.Replace(page.text, "{{picasareview|" + m.Groups[1].ToString() + "|" + m.Groups[2].ToString() + "}}");
                                        SavePage(page, "Converting old LicenseReview tag into picasareview tag");
                                        break;
                                    }
                                    else
                                    {
                                        page.text += "\n{{picasareview}}\n";
                                    }
                                }

                                bool success = false;
                                do
                                {
                                    File.Delete("temp_wiki_image");
                                    File.Delete("temp_picasa_image");
                                    File.Delete("temp_picasa_image_full");

                                    string licenseName, mediaUrl;

                                    bool reviewUnnecessary = false;
                                    if (CheckIfReviewUnnecessary(page))
                                    {
                                        // Keep running so we can upload the original version, break out later
                                        // (unless it has an OTRS tag, in which case we shouldn't raise the resolution,
                                        // or is Flickr reviewed, in which case only a lower-resolution version may
                                        // be released on Flickr)
                                        reviewUnnecessary = true;
                                        success = true;
                                        if (HasOtrsTag(page) || IsFlickrReviewed(page)) continue;
                                    }

                                    if (!FetchPicasaImageInfo(page, out licenseName, out mediaUrl))
                                    {
                                        failureReason = "could not retrieve image information from Picasa";
                                        continue;
                                    }

                                    if (!reviewUnnecessary && !CheckLicense(page, licenseName))
                                    {
                                        failureReason = "image license on Picasa is invalid";
                                        continue;
                                    }
                                    string licenseChangedFrom = null, licenseChangedTo = null;
                                    if (!reviewUnnecessary && !UpdateLicense(page, licenseName, out licenseChangedFrom, out licenseChangedTo))
                                    {
                                        failureReason = "could not recognize license on Commons page";
                                        continue;
                                    }

                                    string mediaUrlFull = new Regex("^(.*)/([^/]*)$").Replace(mediaUrl, "${1}/d/${2}");
                                    if (!WgetToFile(mediaUrlFull, "temp_picasa_image_full"))
                                    {
                                        failureReason = "license matches and is valid - but could not retrieve full size image from Picasa";
                                        continue;
                                    }

                                    page.DownloadImage("temp_wiki_image");
                                    if (!FilesAreIdentical("temp_picasa_image_full", "temp_wiki_image"))
                                    {
                                        // Upload original full res version
                                        if (!UploadOriginalVersion(out failureReason, page, mediaUrl, "temp_wiki_image", "temp_picasa_image_full", /*fetchThumbnailVersion*/true, /*allowWikiBigger*/false))
                                        {
                                            continue;
                                        }
                                    }

                                    if (!reviewUnnecessary)
                                    {
                                        // It matches! Good to go.
                                        UpdateReviewTagSuccess(page, licenseChangedFrom, licenseChangedTo);
                                        success = true;
                                    }
                                } while (false);

                                if (!success)
                                {
                                    UpdateReviewTagFailure(page, failureReason);
                                }
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("Encountered exception: " + e.Message);
                                Console.WriteLine("Retrying...");
                                continue;
                            }

                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Encountered exception: " + e.Message);
                }
                Console.WriteLine("Total pages processed: " + totalPagesProcessed);
                Console.WriteLine("Sleeping for 2 minutes...");
                Thread.Sleep(new TimeSpan(0, 2, 0));
            }
        }

        private static bool UploadOriginalVersion(out string failureReason, Page page, string mediaUrl,
                                                  string wikiImageFilename, string picasaImageFilename,
                                                  bool fetchThumbnailVersion, bool allowWikiBigger)
        {
            // if (!wikiImageFilename.ToLower().EndsWith(".jpg") && !wikiImageFilename.ToLower().EndsWith(".jpeg") &&
            //     !wikiImageFilename.ToLower().EndsWith(".png"))
            // {
            //     failureReason = "Cannot compare non-bitmap files to original source - requires manual validation";
            //     return false;
            // }

            failureReason = null;

            Bitmap wikiBitmap = new Bitmap(wikiImageFilename);

            // First have the Picasa server resize to the desired size - this will
            // ensure exactly the same resizing algorithm is used.
            string thumbnailUrl =
                new Regex("^(.*)/([^/]*)$").Replace(mediaUrl, "${1}/s" + wikiBitmap.Width + "/${2}");

            string filename = "temp_picasa_image";
            if (!fetchThumbnailVersion || !WgetToFile(thumbnailUrl, filename))
            {
                filename = picasaImageFilename;
            }
            Bitmap picasaBitmap = new Bitmap(filename);

            if (wikiBitmap.Width < picasaBitmap.Width ||
                wikiBitmap.Height < picasaBitmap.Height)
            {
                // Couldn't get version of same size from server - stretch to fit
                Bitmap newPicasaBitmap = new Bitmap(picasaBitmap, wikiBitmap.Width, wikiBitmap.Height);
                picasaBitmap.Dispose();
                picasaBitmap = newPicasaBitmap;
            }

            bool wikiBitmapIsBigger = false;
            if (wikiBitmap.Width >= picasaBitmap.Width ||
                wikiBitmap.Height >= picasaBitmap.Height)
            {
                if (allowWikiBigger)
                {
                    wikiBitmapIsBigger = true;
                    Bitmap newWikiBitmap = new Bitmap(wikiBitmap, picasaBitmap.Width, picasaBitmap.Height);
                    wikiBitmap.Dispose();
                    wikiBitmap = newWikiBitmap;
                }
                else
                {
                    // Wiki version is bigger, something odd going on, skip it
                    wikiBitmap.Dispose();
                    picasaBitmap.Dispose();
                    failureReason = "license matches and is valid - but Commons version is of a different size than the Picasa version, may have been edited locally";
                    return false;
                }
            }

            double avgDiff = ImagesMeanSquareDifference(wikiBitmap, picasaBitmap);

            wikiBitmap.Dispose();
            picasaBitmap.Dispose();

            if (avgDiff >= 0.032 && avgDiff < 0.10)
            {
                failureReason = "license matches and is valid - but Picasa and Commons image were not a close enough match";
                return false;
            }
            else if (avgDiff < 0.032)
            {
                // Got an approximate match, need to upload the full-size version
                // (unless we've done so before and were reverted)
                PageList pageHistory = new PageList(page.site);
                pageHistory.FillFromPageHistory(page.title, int.MaxValue);
                bool alreadyUploaded = false;
                foreach (Page revision in pageHistory)
                {
                    if (revision.lastUser == username && revision.comment.Contains("uploaded a new version"))
                    {
                        alreadyUploaded = true;
                    }
                }

                if (!alreadyUploaded && !wikiBitmapIsBigger)
                {
                    string saveText = page.text;
                    page.UploadImage(picasaImageFilename,
                                     "Uploading version from source, revert me if incorrect",
                                     "", "", "");
                    // Set back to current wikitext
                    page.Load();
                    page.text = saveText;
                }
                return true;
            }
            else
            {
                failureReason = "license matches and is valid - but Picasa and Commons image do not match";
                return false;
            }
        }

        static Regex tagRegex = new Regex("{{picasareview}}", RegexOptions.IgnoreCase);
        static Regex tagCompletedRegex = new Regex("({{picasareview\\|[^|]*\\|[^}]*}})|({{picasareview\\|[^|]*\\|[^|]*\\|[^}]*}})", RegexOptions.IgnoreCase);

        private static void UpdateReviewTagFailure(Page page, string failureReason)
        {
            string reviewedText = "{{User:Picasa Review Bot/reviewed-error|~~~~~|" + failureReason + "}}";
            string editSummary = "Unable to confirm license, marking for human review";
            if (failureReason != null)
            {
                editSummary += " (" + failureReason + ")";
            }

            string newPageText = tagRegex.Replace(page.text, reviewedText);
            Regex oldReviewedErrorTagRegex = new Regex("\\{\\{User:Picasa Review Bot/reviewed-error\\|[^|}]*\\}\\}", RegexOptions.IgnoreCase);

            if (oldReviewedErrorTagRegex.Match(newPageText).Success)
            {
                newPageText = oldReviewedErrorTagRegex.Replace(newPageText, reviewedText);
                editSummary = "Updating to add reason for automated review failure";
                if (failureReason != null)
                {
                    editSummary += " (" + failureReason + ")";
                }
            }

            Regex newReviewedErrorTagRegex = new Regex("\\{\\{User:Picasa Review Bot/reviewed-error\\|[^|]*\\|([^|}]*)\\}\\}", RegexOptions.IgnoreCase);
            Match m;
            if ((m = newReviewedErrorTagRegex.Match(newPageText)).Success)
            {
                string oldFailureReason = m.Groups[1].ToString();
                if (oldFailureReason != failureReason)
                {
                    newPageText = newReviewedErrorTagRegex.Replace(newPageText, reviewedText);
                    editSummary = "Updating reason for automated review failure to ";
                    if (failureReason != null)
                    {
                        editSummary += "'" + failureReason + "'";
                    }
                }
            }
            
            if (page.text != newPageText)
            {
                page.text = newPageText;
                SavePage(page, editSummary);
            }
            else
            {
                Console.WriteLine("No change (" + failureReason + ")");
            }
        }

        private static void UpdateReviewTagSuccess(Page page, string licenseChangedFrom, string licenseChangedTo)
        {
            string newPageText;
            string reviewedText = "{{picasareview|Picasa Review Bot|~~~~~";
            if (licenseChangedFrom != null)
            {
                reviewedText += "|changed=" + licenseChangedFrom;
                if (page.text.Contains("{{" + licenseChangedFrom + "}}"))
                {
                    page.text = page.text.Replace("{{" + licenseChangedFrom + "}}", "{{" + licenseChangedTo + "}}");
                }
                else if (page.text.Contains("{{self|" + licenseChangedFrom + "}}"))
                {
                    page.text = page.text.Replace("{{self|" + licenseChangedFrom + "}}", "{{self|" + licenseChangedTo + "}}");
                }
            }
            reviewedText += "}}";

            newPageText = tagRegex.Replace(page.text, reviewedText);
            newPageText = new Regex("\\{\\{User:Picasa Review Bot/reviewed-error\\|[^}]*\\}\\}", RegexOptions.IgnoreCase).
                              Replace(newPageText, reviewedText);
            newPageText = new Regex("\\{\\{User:Picasa Review Bot/reviewed-error\\|[^|]*\\}\\|[^}]*\\}", RegexOptions.IgnoreCase).
                              Replace(newPageText, reviewedText);
            if (page.text != newPageText)
            {
                page.text = newPageText;
                if (licenseChangedFrom != null)
                {
                    SavePage(page, "Updated license based on Picasa Web Albums source page, adding reviewed tag");
                }
                else
                {
                    SavePage(page, "License confirmed for Picasa Web Albums image, adding reviewed tag");
                }
            }
        }

        private static void SavePage(Page page, string editSummary)
        {
            page.Save(editSummary, /*isMinorEdit*/false);
            Console.WriteLine(editSummary);
        }

        private static double ImagesMeanSquareDifference(Bitmap wikiBitmap, Bitmap picasaBitmap)
        {
            //double maxDiff = 0.0;
            long meanSquareDiff = 0;
            BitmapData wikiData = wikiBitmap.LockBits(new Rectangle(0, 0, wikiBitmap.Width, wikiBitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            BitmapData picasaData = picasaBitmap.LockBits(new Rectangle(0, 0, wikiBitmap.Width, wikiBitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            unsafe
            {
                for (int y = 0; y < wikiData.Height; y++)
                {
                    byte* wikiPtr = (byte*)wikiData.Scan0 + wikiData.Stride * y;
                    byte* picasaPtr = (byte*)picasaData.Scan0 + picasaData.Stride * y;
                    for (int x = 0; x < wikiBitmap.Width; x++)
                    {
                        int diff;
                        diff = ((int)*wikiPtr) - *picasaPtr;
                        meanSquareDiff += diff * diff;
                        wikiPtr++; picasaPtr++;
                        diff = ((int)*wikiPtr) - *picasaPtr;
                        meanSquareDiff += diff * diff;
                        wikiPtr++; picasaPtr++;
                        diff = ((int)*wikiPtr) - *picasaPtr;
                        meanSquareDiff += diff * diff;
                        wikiPtr++; picasaPtr++;
                        //double diff = Math.Sqrt(squareDiff);
                        //if (diff > maxDiff) maxDiff = diff;
                    }
                }
            }
            picasaBitmap.UnlockBits(picasaData);
            wikiBitmap.UnlockBits(wikiData);

            double avgDiff = Math.Sqrt(meanSquareDiff) / wikiBitmap.Width / wikiBitmap.Height;
            return avgDiff;
        }

        private static bool FilesAreIdentical(string file1, string file2)
        {
            using (Stream mediaReaderPicasa = File.Open(file1, FileMode.Open))
            {
                using (Stream mediaReaderWiki = File.Open(file2, FileMode.Open))
                {
                    // Compare files
                    int c1, c2;
                    while (true)
                    {
                        c1 = mediaReaderPicasa.ReadByte();
                        c2 = mediaReaderWiki.ReadByte();
                        if (c1 != c2)
                        {
                            return false;
                        }
                        if (c1 == -1 || c2 == -1) break;
                    }
                }
            }
            return true;
        }

        private static bool CheckLicense(Page page, string licenseName)
        {
            if (licenseName != "Attribution License" &&
                    licenseName != "Attribution-Share Alike")
            {
                return false;
            }
            return true;
        }

        private static bool UpdateLicense(Page page, string licenseName, out string licenseChangedFrom, out string licenseChangedTo)
        {
            licenseChangedFrom = licenseChangedTo = null;

            if (licenseName == "Attribution License" &&
                !page.text.ToLower().Contains("{{cc-by-3.0}}") &&
                !page.text.ToLower().Contains("{{self|cc-by-3.0}}") &&
                !page.text.ToLower().Contains("{{cc-by}}") &&
                !new Regex("\\{\\{cc-by-3\\.0 *\\|[^}]*\\}\\}", RegexOptions.IgnoreCase).Match(page.text).Success &&
                !new Regex("\\{\\{cc-by *\\|[^}]*\\}\\}", RegexOptions.IgnoreCase).Match(page.text).Success)
            {
                licenseChangedTo = "cc-by-3.0";
            }
            if (licenseName == "Attribution-Share Alike" &&
                !page.text.ToLower().Contains("{{cc-by-sa-3.0}}") &&
                !page.text.ToLower().Contains("{{self|cc-by-sa-3.0}}") &&
                !page.text.ToLower().Contains("{{cc-by-sa}}") &&
                !new Regex("\\{\\{cc-by-sa-3\\.0 *\\|[^}]*\\}\\}", RegexOptions.IgnoreCase).Match(page.text).Success &&
                !new Regex("\\{\\{cc-by-sa *\\|[^}]*\\}\\}", RegexOptions.IgnoreCase).Match(page.text).Success)
            {
                licenseChangedTo = "cc-by-sa-3.0";
            }
            if (licenseChangedTo != null)
            {
                string[] licenseList = new string[] {
                            "cc-by",
                            "cc-by-sa",
                            "cc-by-1.0",
                            "cc-by-2.0",
                            "cc-by-2.5",
                            "cc-by-3.0",
                            "cc-by-sa-1.0",
                            "cc-by-sa-2.0",
                            "cc-by-sa-2.5",
                            "cc-by-sa-2.5,1.0",
                            "cc-by-sa-2.5,2.0,1.0",
                            "cc-by-sa-3.0",
                            "gfdl",
                            "GFDL",
                            "GFDL",
                        };
                foreach (string license in licenseList)
                {
                    if (page.text.Contains("{{" + license + "}}"))
                    {
                        licenseChangedFrom = license;
                        break;
                    }
                    else if (page.text.Contains("{{self|" + license + "}}"))
                    {
                        licenseChangedFrom = license;
                        break;
                    }
                }
                if (licenseChangedFrom == null)
                {
                    return false;
                }
            }
            return true;
        }

        static Regex picasaUrlRegex = new Regex("https?://picasaweb\\.google(\\.[\\.a-z]+)/([^/ ]+).*#([0-9]+)", RegexOptions.IgnoreCase);
        static Regex albumIdRegex = new Regex("\"albumId\":\"([0-9]+)\"");
        static Regex licenseRegex = new Regex("gphoto:license id='[0-9]+' name='([^']+)'");
        static Regex mediaUrlRegex = new Regex("media:content url='([^']+)'");
        static Regex summaryTextRegex = new Regex("<summary type='text'>([^<]*)</summary>");

        private static bool FetchPicasaImageInfo(Page page, out string licenseName, out string mediaUrl)
        {
            licenseName = mediaUrl = null;

            Match m;
            m = picasaUrlRegex.Match(page.text);
            if (!m.Success)
            {
                UpdateSourceField(page);
                m = picasaUrlRegex.Match(page.text);
                if (!m.Success)
                {
                    return false;
                }
            }
            string url = m.Groups[0].ToString();
            url = url.Replace(m.Groups[1].ToString(), ".com"); // Use English version
            string userId = m.Groups[2].ToString();
            string photoId = m.Groups[3].ToString();

            string picasaImagePage = WgetToString(url);
            if (picasaImagePage == null) return false;
            m = albumIdRegex.Match(picasaImagePage);
            if (!m.Success)
            {
                m = new Regex("var _album = {id:'([0-9]+)'").Match(picasaImagePage);
                if (!m.Success)
                {
                    return false;
                }
            }
            string albumId = m.Groups[1].ToString();

            string summary;
            return GetImageInfo(userId, albumId, photoId, out licenseName, out mediaUrl, out summary);
        }

        private static bool GetImageInfo(string userId, string albumId, string photoId, out string licenseName, out string mediaUrl, out string summary)
        {
            licenseName = mediaUrl = null;
            summary = "";

            string apiUrl = "http://picasaweb.google.com/data/feed/api/user/" + userId + "/albumid/" + albumId + "/photoid/" + photoId;
            string picasaApiPage = WgetToString(apiUrl);
            if (picasaApiPage == null) return false;

            Match m = licenseRegex.Match(picasaApiPage);
            if (!m.Success) return false;
            licenseName = m.Groups[1].ToString();
            m = mediaUrlRegex.Match(picasaApiPage);
            if (!m.Success) return false;
            mediaUrl = m.Groups[1].ToString();

            m = new Regex("<title type='text'>([^<]*)</title>").Match(picasaApiPage);
            if (m.Success) summary += m.Groups[1].ToString() + " ";
            m = new Regex("<subtitle type='text'>([^<]*)</subtitle>", RegexOptions.Singleline).Match(picasaApiPage);
            if (m.Success) summary += m.Groups[1].ToString();

            return true;
        }

        static Regex picasaAlbumUrlRegex = new Regex("https?://picasaweb\\.google(\\.[\\.a-z]+)/([^/ ]+)/([^/ \\]\\n#]+)", RegexOptions.IgnoreCase);
        static Regex picasaUserUrlRegex  = new Regex("https?://picasaweb\\.google(\\.[\\.a-z]+)/([^/ \\]\\n]+)", RegexOptions.IgnoreCase);
        static Regex userPageAlbumIdRegex = new Regex("<gphoto:id>([0-9]+)</gphoto:id><gphoto:name>([^<]*)</gphoto:name>");
        static Regex albumPageImageIdRegex = new Regex("<gphoto:id>([0-9]+)</gphoto:id>");

        struct PhotoInfo
        {
            public string mediaUrl;
            public string albumName;
            public string albumId;
            public string summary;
        }

        private static Dictionary<string, PhotoInfo> idToPhotoInfo = new Dictionary<string, PhotoInfo>();

        private static void UpdateSourceField(Page page)
        {
            Match m;
            if ((m = picasaAlbumUrlRegex.Match(page.text)).Success ||
                (m = picasaUserUrlRegex.Match(page.text)).Success)
            {
                page.DownloadImage("temp_wiki_image");

                string userId = m.Groups[2].ToString();
                string chosenAlbumName = m.Groups.Count >= 3 ? m.Groups[3].ToString() : null;
                
                // A common existing error
                if (chosenAlbumName == "Eakins")
                {
                    chosenAlbumName = "EakinsThomas";
                }

                string userApiUrl = "http://picasaweb.google.com/data/feed/api/user/" + userId;
                string userApiPage = WgetToString(userApiUrl);
                if (userApiPage == null) return;

                Console.WriteLine("Searching for image URL. Retrieving info about candidate images...");
                List<string> photoIds = new List<string>();
                foreach (Match m2 in userPageAlbumIdRegex.Matches(userApiPage))
                {
                    string albumName = m2.Groups[2].ToString();
                    if (chosenAlbumName == null || albumName == chosenAlbumName)
                    {
                        string albumId = m2.Groups[1].ToString();
                        string albumApiUrl = "http://picasaweb.google.com/data/feed/api/user/" + userId + "/albumid/" + albumId;
                        string albumApiPage = WgetToString(albumApiUrl);

                        foreach (Match m3 in albumPageImageIdRegex.Matches(albumApiPage))
                        {
                            string photoId = m3.Groups[1].ToString();
                            string licenseName = null;
                            PhotoInfo info = new PhotoInfo();
                            info.albumId = albumId;
                            info.albumName = albumName;

                            photoIds.Add(photoId);
                            if (idToPhotoInfo.ContainsKey(photoId)) continue;
                            if (!GetImageInfo(userId, albumId, photoId, out licenseName, out info.mediaUrl, out info.summary))
                            {
                                photoIds.Remove(photoId);
                                continue;
                            }
                            idToPhotoInfo.Add(photoId, info);
                        }
                    }
                }

                // Sort by the longest common substring between the summary and filename
                photoIds.Sort(new Comparison<string>(delegate(string leftId, string rightId)
                    {
                        string titleNoPrefix = page.title.Substring("File:".Length);
                        return LongestCommonSubstring(idToPhotoInfo[rightId].summary, titleNoPrefix)
                              .CompareTo(LongestCommonSubstring(idToPhotoInfo[leftId].summary, titleNoPrefix));
                    }));

                Console.WriteLine("Doing image comparisons...");
                foreach (string photoId in photoIds)
                {
                    PhotoInfo info = idToPhotoInfo[photoId];
                    string photoCachedFilename = "photo" + photoId;

                    while (!File.Exists(photoCachedFilename) || new FileInfo(photoCachedFilename).Length == 0)
                    {
                        string mediaUrlFull = new Regex("^(.*)/([^/]*)$").Replace(info.mediaUrl, "${1}/d/${2}");
                        Console.WriteLine("Fetching photo with ID " + photoId + "...");
                        WgetToFile(mediaUrlFull, photoCachedFilename);
                    }

                    if (FilesAreIdentical(photoCachedFilename, "temp_wiki_image"))
                    {
                        UpdateSource(page, userId, info.albumName, photoId);
                        return;
                    }

                    string failureReason;
                    if (UploadOriginalVersion(out failureReason, page, info.mediaUrl, "temp_wiki_image", photoCachedFilename, /*fetchThumbnailVersion*/false, /*allowWikiBigger*/true))
                    {
                        UpdateSource(page, userId, info.albumName, photoId);
                        return;
                    }
                }

                Console.WriteLine("Image not found");
            }
            else
            {
                if (!page.text.Contains("waldemar"))
                {
                    // For debugging, catch ones where we couldn't figure out the Picasa URL
                    //Debugger.Break();
                }
            }
        }

        private static int LongestCommonSubstring(string left, string right)
        {
            int longestLength = 0;
            for (int i = 0; i < left.Length; i++)
            {
                for (int j = 0; j < right.Length; j++)
                {
                    for (int k = 0; ; k++)
                    {
                        if (i + k >= left.Length ||
                            j + k >= right.Length ||
                            Char.ToLower(left[i + k]) != Char.ToLower(right[j + k]))
                        {
                            if (k > longestLength)
                            {
                                longestLength = k;
                            }
                            break;
                        }
                    }
                }
            }
            return longestLength;
        }

        private static void UpdateSource(Page page, string userId, string albumName, string photoId)
        {
            string sourceUrl = "http://picasaweb.google.com/" + userId + "/" + albumName + "#" + photoId;
            if (page.text.Contains("|Source="))
                page.text = page.text.Replace("|Source=", "|Source=[" + sourceUrl + "] (found automatically by [[User:Picasa Review Bot]]) ");
            else
                page.text += "\n'''Source URL''' (found automatically by [[User:Picasa Review Bot]]): [" + sourceUrl + "]\n";

            SavePage(page, "Located and added precise source URL");
        }

        private static bool CheckIfReviewUnnecessary(Page page)
        {
            string newPageText;
            bool unnecessary = false;
            string unnecessaryReason = "";

            if (page.text.ToLower().Contains("{{picasareviewunnecessary}}"))
            {
                // Already marked unnecessary, assume it is
                return true;
            }

            if (IsFlickrReviewed(page))
            {
                unnecessary = true;
                unnecessaryReason = "Also on Flickr and already Flickr reviewed";
            }
            if (HasOtrsTag(page))
            {
                unnecessary = true;
                unnecessaryReason = "Has OTRS tag";
            }
            foreach (string tag in new string[] { "{{pd-usgov", "{{pd-art}}", "{{pd-art|", "{{pd-scan}}", "{{pd-scan|", "{{pd-old", "{{pd-us}}", "{{anonymous-eu}}" })
            {
                if (page.text.ToLower().Contains(tag))
                {
                    unnecessary = true;
                    unnecessaryReason = "Is public domain";
                }
            }
            if (unnecessary)
            {
                newPageText = tagRegex.Replace(page.text, "{{picasareviewunnecessary}}");
                newPageText = new Regex("\\{\\{User:Picasa Review Bot/reviewed-error\\|[^}]*\\}\\}", RegexOptions.IgnoreCase).
                                  Replace(newPageText, "{{picasareviewunnecessary}}");
                if (page.text != newPageText)
                {
                    page.text = newPageText;
                    SavePage(page, "Marked {{picasareviewunnecessary}}: " + unnecessaryReason);
                }
                return true;
            }
            return false;
        }

        private static bool IsFlickrReviewed(Page page)
        {
            foreach (string tag in new string[] {
                        "{{flickrreview|",
                        "{{user:flickr upload bot/upload",
                        "{{user:flickreviewr/reviewed-pass"})
            {
                if (page.text.ToLower().Contains(tag))
                {
                    return true;
                }
            }
            return false;
        }

        static bool HasOtrsTag(Page page)
        {
            foreach (string tag in new string[] {"{{otrs",
                                                 "{{permissionotrs|",
                                                 "{{permissionotrs-id|",
                                                 "{{wikiportrait|",
                                                 "{{gary lee todd permission",
                                                 "{{разрешение otrs|"})
            {
                if (page.text.ToLower().Contains(tag))
                {
                    return true;
                }
            }
            return false;
        }

        const int MsPerApiCall = 0; // 500;

        private static Stream WGet(string url)
        {
            HttpWebResponse response = null;
            do
            {
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    response = (HttpWebResponse)request.GetResponse();
                    if (response != null)
                    {
                        break;
                    }
                }
                catch (WebException e)
                {
                    if (e.Status == WebExceptionStatus.ProtocolError)
                    {
                        return null;
                    }
                    response = null;
                }
                Thread.Sleep(5000);
            }
            while (true);

            Stream responseStream = response.GetResponseStream();

            Thread.Sleep(MsPerApiCall);

            return responseStream;
        }

        private static string WgetToString(string url)
        {
            string result;
            Stream readerStream = WGet(url);
            if (readerStream == null) return null;
            using (StreamReader reader = new StreamReader(readerStream))
            {
                result = reader.ReadToEnd();
            }
            return result;
        }

        private static bool WgetToFile(string url, string filename)
        {
            using (Stream mediaWriter = File.Open(filename, FileMode.Create))
            {
                using (Stream mediaReaderPicasa = WGet(url))
                {
                    if (mediaReaderPicasa == null)
                    {
                        return false;
                    }
                    else
                        while (true)
                        {
                            int c = mediaReaderPicasa.ReadByte();
                            if (c == -1) break;
                            mediaWriter.WriteByte((byte)c);
                        }
                }
            }
            return true;
        }
    }
}
