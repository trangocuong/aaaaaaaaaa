using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace checklog
{
    // =============================================================
    // DATA MODEL
    // =============================================================
    public class LotResult
    {
        public string PartNo { get; set; } = "";
        public string LotNo { get; set; } = "";
        public string FabSite { get; set; } = "";
        public string Tier { get; set; } = "";
        public string Option { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string SmtLine { get; set; } = "";
        public string Eqpid { get; set; } = "";

        public int InQty { get; set; }
        public int FailCount { get; set; }
        public double Yield { get; set; }

        public List<ScrapItem> FailList { get; set; } = new List<ScrapItem>();
    }

    public class ScrapItem
    {
        public string Code { get; set; }
        public string Serial { get; set; }
    }

    // =============================================================
    // CORE LOGIC
    // =============================================================
    public static class LogParser
    {
        public static List<LotResult> ParseLotByScrapUp(string path)
        {
            var lines = File.ReadAllLines(path).ToList();
            var results = new List<LotResult>();

            // DANH SÁCH PHÒNG CHỜ (Chứa các Lot đã In nhưng chưa Out)
            var pendingLots = new List<LotResult>();

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];

                // ---------------------------------------------------------
                // 1. SỰ KIỆN LOT IN
                // ---------------------------------------------------------
                if (line.Contains("Dialog_LotIn") && line.Contains("GetLotData"))
                {
                    var newLot = new LotResult();
                    ParseHeaderInfo(lines, i, newLot);

                    if (!string.IsNullOrEmpty(newLot.LotNo))
                    {
                        var existing = pendingLots.FirstOrDefault(x => x.LotNo == newLot.LotNo);
                        if (existing != null) pendingLots.Remove(existing);
                        pendingLots.Add(newLot);
                    }
                    else
                    {
                        newLot.LotNo = "UNKNOWN_" + i;
                        pendingLots.Add(newLot);
                    }
                }

                // ---------------------------------------------------------
                // 2. SỰ KIỆN SCRAP / TRACK OUT
                // ---------------------------------------------------------
                else if (IsScrapInfoHeader(lines, i))
                {
                    string lotIdInScrap = FindLotIdInScrapBlock(lines, i);

                    if (string.IsNullOrEmpty(lotIdInScrap)) continue;
                    if (results.Any(r => r.LotNo == lotIdInScrap)) continue; // Bỏ qua nếu đã xử lý (duplicate log)

                    LotResult targetLot = pendingLots.FirstOrDefault(x => x.LotNo == lotIdInScrap);

                    if (targetLot == null)
                    {
                        targetLot = new LotResult { LotNo = lotIdInScrap };
                        string partId = FindValueInBlock(lines, i, 20, "A PARTID");
                        if (!string.IsNullOrEmpty(partId)) targetLot.PartNo = partId;
                    }
                    else
                    {
                        pendingLots.Remove(targetLot);
                    }

                    ParseScrapInfo(lines, i, targetLot);

                    if (targetLot.FailList.Count == 0 && targetLot.FailCount == 0)
                    {
                        targetLot.FailList.Add(new ScrapItem { Code = "No Fail", Serial = "-" });
                    }

                    if (targetLot.FailList.Count > targetLot.FailCount)
                        targetLot.FailCount = targetLot.FailList.Count;

                    if (targetLot.InQty > 0)
                        targetLot.Yield = (double)(targetLot.InQty - targetLot.FailCount) / targetLot.InQty * 100;
                    else
                        targetLot.Yield = 0;

                    if (!targetLot.LotNo.StartsWith("UNKNOWN_"))
                    {
                        results.Add(targetLot);
                    }
                }

                // ---------------------------------------------------------
                // 3. [MỚI] SỰ KIỆN EQPID (DEBUG LOG) - Xử lý độc lập
                // Mẫu: VID[2001] DATA[TH-H303] (EQP) ... VID[2004] DATA[LOTID]
                // ---------------------------------------------------------
                else if (line.Contains("VID[2001]") && line.Contains("DATA["))
                {
                    // Bước 1: Lấy EQPID
                    string eqpId = "";
                    var m = Regex.Match(line, @"DATA\[(.*?)\]");
                    if (m.Success) eqpId = m.Groups[1].Value.Trim();

                    // Bước 2: Tìm LotID ở các dòng ngay sau đó (VID[2004])
                    string lotId = "";
                    for (int k = i + 1; k < Math.Min(i + 10, lines.Count); k++)
                    {
                        if (lines[k].Contains("VID[2004]") && lines[k].Contains("DATA["))
                        {
                            var m2 = Regex.Match(lines[k], @"DATA\[(.*?)\]");
                            if (m2.Success)
                            {
                                lotId = m2.Groups[1].Value.Trim();
                                break; // Tìm thấy thì dừng ngay
                            }
                        }
                    }

                    // Bước 3: Cập nhật vào kết quả
                    if (!string.IsNullOrEmpty(lotId) && !string.IsNullOrEmpty(eqpId))
                    {
                        // Ưu tiên tìm trong danh sách Results (đã Scarp xong)
                        // Lấy cái cuối cùng (LastOrDefault) để đảm bảo đúng lot mới nhất nếu có trùng tên
                        var processedLot = results.LastOrDefault(x => x.LotNo == lotId);
                        if (processedLot != null)
                        {
                            processedLot.Eqpid = eqpId;
                        }
                        else
                        {
                            // Nếu chưa Scrap xong (còn trong Pending) thì cập nhật Pending
                            var pending = pendingLots.FirstOrDefault(x => x.LotNo == lotId);
                            if (pending != null) pending.Eqpid = eqpId;
                        }
                    }
                }
            }

            return results;
        }

        // =============================================================
        // CÁC HÀM PARSE CHI TIẾT
        // =============================================================

        private static void ParseHeaderInfo(List<string> lines, int startIndex, LotResult lot)
        {
            // Bỏ giới hạn cứng 80 dòng, cho phép quét xa hơn nhưng có điểm dừng chặt chẽ
            // Tuy nhiên vẫn nên để một giới hạn an toàn (ví dụ 200) để tránh treo nếu file lỗi
            int limit = Math.Min(startIndex + 200, lines.Count);

            for (int i = startIndex; i < limit; i++)
            {
                string line = lines[i].Trim();

                // =========================================================
                // ĐIỀU KIỆN DỪNG (QUAN TRỌNG)
                // =========================================================

                // 1. Gặp tín hiệu kết thúc bản tin thành công (Dấu hiệu chuẩn nhất)
                if (line.Contains("Recive S14F3 successfully")) break;

                // 2. Gặp sự kiện bắt đầu bản tin mới (Safety net)
                if (i > startIndex && line.Contains("->>Received")) break;

                // 3. Gặp sự kiện màn hình hoặc nút bấm khác
                if (i > startIndex && (line.Contains("[MAIN]") || line.Contains("Dialog_LotIn"))) break;

                // =========================================================
                // PARSE DỮ LIỆU
                // =========================================================

                if (line.Contains("A PARTID")) lot.PartNo = GetNextAValue(lines, i);
                else if (string.IsNullOrEmpty(lot.LotNo) && line.Contains("A LOTINFO"))
                {
                    lot.LotNo = GetNextAValue(lines, i);
                }
                // Lấy số lượng
                else if (line.Contains("A QTY") || line.Contains("A OQTY"))
                {
                    if (int.TryParse(GetNextAValue(lines, i), out int q)) lot.InQty = q;
                }

                // Các thông tin khác
                else if (line.Contains("A FABSITE")) lot.FabSite = GetNextAValue(lines, i);
                else if (line.Contains("A TIER")) lot.Tier = GetNextAValue(lines, i);
                else if (line.Contains("A OPTCODE")) lot.Option = GetNextAValue(lines, i);
                else if (line.Contains("A PCBVENDOR")) lot.Vendor = GetNextAValue(lines, i);
                else if (line.Contains("A ASSYLINE")) lot.SmtLine = GetNextAValue(lines, i);
            }
        }

        private static void ParseScrapInfo(List<string> lines, int startIndex, LotResult lot)
        {
            // Vẫn giữ limit để tránh loop vô hạn, nhưng sẽ break sớm
            int limit = Math.Min(startIndex + 60, lines.Count);

            for (int i = startIndex; i < limit; i++)
            {
                string line = lines[i].Trim();

                // =========================================================
                // CÁC ĐIỀU KIỆN DỪNG (QUAN TRỌNG)
                // =========================================================

                // 1. Gặp Lot mới vào
                if (i > startIndex && line.Contains("Dialog_LotIn")) break;

                // 2. Gặp tín hiệu kết thúc bản tin hiện tại (Chốt chặn quan trọng nhất)
                if (line.Contains("Recive S14F3 successfully")) break;

                // 3. Gặp tín hiệu bắt đầu bản tin tiếp theo (Đề phòng trường hợp lặp hoặc sát nhau)
                if (i > startIndex && line.Contains("->>Received")) break;
                if (i > startIndex && line.Contains("A LOT") && !line.Contains("LOTINFO")) break;

                // =========================================================
                // XỬ LÝ DỮ LIỆU
                // =========================================================

                // Lấy tổng lỗi
                if (line.Contains("A SCRAP_CNT"))
                {
                    if (int.TryParse(GetNextAValue(lines, i), out int cnt))
                    {
                        lot.FailCount = cnt;
                        // Nếu có lỗi (cnt > 0) mà chưa đọc được chi tiết -> Tạo placeholder
                        if (lot.FailList.Count == 0 && cnt > 0)
                        {
                            for (int k = 0; k < cnt; k++)
                                lot.FailList.Add(new ScrapItem { Code = "N/A", Serial = "Check Log" });
                        }
                    }
                }

                // Lấy chi tiết lỗi
                if (line.Contains("SCRAP_CODE="))
                {
                    var matches = Regex.Matches(line, @"SCRAP_CODE=(?<code>\d+)\s+SERIAL=(?<serial>[A-Za-z0-9]+)");

                    // Nếu tìm thấy code thật, xóa các placeholder "N/A" đi
                    if (matches.Count > 0 && lot.FailList.Any(x => x.Code == "N/A"))
                        lot.FailList.Clear();

                    foreach (Match m in matches)
                    {
                        lot.FailList.Add(new ScrapItem
                        {
                            Code = m.Groups["code"].Value,
                            Serial = m.Groups["serial"].Value
                        });
                    }
                }
            }
        }

        private static string FindLotIdInScrapBlock(List<string> lines, int startIndex)
        {
            int limit = Math.Min(startIndex + 25, lines.Count);
            for (int i = startIndex; i < limit; i++)
            {
                string line = lines[i];
                if ((line.Contains("A LOT") && !line.Contains("LOTINFO")) ||
                    (line.Contains("A SCRAP_INFO") && !Regex.IsMatch(line, @"SCRAP_INFO\d+")))
                {
                    string val = GetNextAValue(lines, i);
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            return "";
        }

        private static bool IsScrapInfoHeader(List<string> lines, int index)
        {
            string line = lines[index];
            if (line.Contains("A SCRAP_INFO") && !Regex.IsMatch(line, @"SCRAP_INFO\d+")) return true;
            return false;
        }

        private static string GetNextAValue(List<string> lines, int currentIndex)
        {
            for (int i = currentIndex + 1; i < Math.Min(currentIndex + 10, lines.Count); i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("L ") || string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("SCRAP_INFO") || line.Contains("SCRAP_CNT") || line.Contains("SCRAP_LIST")) continue;

                string val = ExtractAValue(line);
                if (!string.IsNullOrEmpty(val)) return val;
            }
            return "";
        }

        private static string FindValueInBlock(List<string> lines, int startIndex, int range, string keyword)
        {
            int limit = Math.Min(startIndex + range, lines.Count);
            for (int i = startIndex; i < limit; i++)
            {
                if (lines[i].Contains(keyword)) return GetNextAValue(lines, i);
            }
            return "";
        }

        private static string ExtractAValue(string line)
        {
            int idx = line.IndexOf(':');
            if (idx >= 0) line = line.Substring(idx + 1);
            var m = Regex.Match(line, @"\bA\s+([^\s\)]+)");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
    }
}