using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Security.Cryptography.Pkcs;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace Praveen.XrmToolbox.P7mAttachmentConverter
{
    public partial class P7mConverterControl : PluginControlBase, IXrmToolBoxPluginControl
    {
        private DataGridView _grid;
        private Button _btnLoad;
        private Button _btnConvert;
        private ProgressBar _progressBar;
        private System.Windows.Forms.Label _statusLabel;

        public P7mConverterControl()
        {
            InitializeComponent();
            BuildUi();
        }

        // ---------- UI SETUP ----------
        private void BuildUi()
        {
            this.Dock = DockStyle.Fill;

            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                FlowDirection = FlowDirection.LeftToRight
            };

            _btnLoad = new Button { Text = "Load P7M Attachments", Width = 180 };
            _btnLoad.Click += (s, e) => LoadAttachments();

            _btnConvert = new Button { Text = "Convert Selected", Width = 150, Enabled = false };
            _btnConvert.Click += (s, e) => ConvertSelected();

            toolbar.Controls.Add(_btnLoad);
            toolbar.Controls.Add(_btnConvert);

            _statusLabel = new System.Windows.Forms.Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Text = "Not connected."
            };

            _progressBar = new ProgressBar
            {
                Dock = DockStyle.Top,
                Height = 18,
                Visible = false
            };

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AllowUserToAddRows = false
                // NOTE: grid-level ReadOnly is intentionally NOT set here.
                // DataGridView.ReadOnly=true overrides every column's own
                // ReadOnly setting, which silently blocked the checkbox
                // column below from ever being toggled. Instead, each
                // non-checkbox column is marked ReadOnly individually.
            };

            _grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                Name = "Selected",
                HeaderText = "",
                Width = 30,
                ReadOnly = false
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "FileName",
                HeaderText = "File Name",
                DataPropertyName = "FileName",
                Width = 300,
                ReadOnly = true
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Source",
                HeaderText = "Source",
                DataPropertyName = "Source",
                Width = 120,
                ReadOnly = true
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "RegardingId",
                HeaderText = "Regarding Record",
                DataPropertyName = "RegardingId",
                Width = 260,
                ReadOnly = true
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Status",
                HeaderText = "Status",
                DataPropertyName = "Status",
                Width = 200,
                ReadOnly = true
            });

            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.CurrentCell is DataGridViewCheckBoxCell)
                {
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };

            this.Controls.Add(_grid);
            this.Controls.Add(_progressBar);
            this.Controls.Add(_statusLabel);
            this.Controls.Add(toolbar);
        }

        // ---------- IXrmToolBoxPluginControl ----------
        public string HelpUrl => null;
        public string RepositoryUrl => null;

        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName = "", object parameter = null)
        {
            base.UpdateConnection(newService, detail, actionName, parameter);
            _statusLabel.Text = $"Connected to: {detail?.ConnectionName}";
        }

        public override void ClosingPlugin(PluginCloseInfo info)
        {
            base.ClosingPlugin(info);
        }

        // ---------- DATA MODEL ----------
        private class AttachmentRow
        {
            public bool Selected { get; set; }
            public string Source { get; set; }
            public Guid AnnotationId { get; set; }
            public string FileName { get; set; }
            public EntityReference RegardingRef { get; set; }
            public string RegardingId => RegardingRef?.Id.ToString() ?? "(none)";
            public string DocumentBody { get; set; }
            public string Status { get; set; } = "Pending";
        }

        private readonly List<AttachmentRow> _rows = new List<AttachmentRow>();
        private BindingSource _bindingSource;

        // ---------- STEP 1: LOAD ----------
        private void LoadAttachments()
        {
            if (Service == null)
            {
                MessageBox.Show("Connect to an organization first.", "Not connected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _rows.Clear();
            _grid.DataSource = null;
            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Marquee;
            _statusLabel.Text = "Retrieving .p7m attachments...";

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Retrieving .p7m attachments...",
                Work = (worker, args) =>
                {
                    var found = new List<AttachmentRow>();

                    // 1) Notes (annotation)
                    var noteQuery = new QueryExpression("annotation")
                    {
                        ColumnSet = new ColumnSet("filename", "documentbody", "objectid", "mimetype", "subject")
                    };
                    noteQuery.Criteria.AddCondition("filename", ConditionOperator.EndsWith, ".p7m");
                    noteQuery.TopCount = 500;

                    foreach (var e in Service.RetrieveMultiple(noteQuery).Entities)
                    {
                        found.Add(new AttachmentRow
                        {
                            Source = "Note",
                            AnnotationId = e.Id,
                            FileName = e.GetAttributeValue<string>("filename"),
                            RegardingRef = e.GetAttributeValue<EntityReference>("objectid"),
                            DocumentBody = e.GetAttributeValue<string>("documentbody")
                        });
                    }

                    // 2) Email attachments (activitymimeattachment) — what
                    //    server-side sync creates for incoming emails.
                    var mailQuery = new QueryExpression("activitymimeattachment")
                    {
                        ColumnSet = new ColumnSet("filename", "body", "objectid", "objecttypecode", "mimetype", "subject")
                    };
                    mailQuery.Criteria.AddCondition("filename", ConditionOperator.EndsWith, ".p7m");
                    mailQuery.TopCount = 500;

                    foreach (var e in Service.RetrieveMultiple(mailQuery).Entities)
                    {
                        var parent = e.GetAttributeValue<EntityReference>("objectid");
                        string typeCode = e.Contains("objecttypecode") ? e["objecttypecode"]?.ToString() : null;
                        if (parent != null && string.IsNullOrEmpty(typeCode)) typeCode = parent.LogicalName;
                        if (string.IsNullOrEmpty(typeCode) || typeCode == "activitypointer") typeCode = "email";

                        found.Add(new AttachmentRow
                        {
                            Source = "Email attachment",
                            AnnotationId = e.Id,
                            FileName = e.GetAttributeValue<string>("filename"),
                            RegardingRef = parent == null ? null : new EntityReference(typeCode, parent.Id),
                            DocumentBody = e.GetAttributeValue<string>("body")
                        });
                    }

                    args.Result = found;
                },
                PostWorkCallBack = (args) =>
                {
                    _progressBar.Visible = false;

                    if (args.Error != null)
                    {
                        MessageBox.Show($"Error retrieving attachments: {args.Error.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        _statusLabel.Text = "Error during retrieval.";
                        return;
                    }

                    _rows.AddRange((List<AttachmentRow>)args.Result);

                    _bindingSource = new BindingSource { DataSource = _rows };
                    _grid.DataSource = _bindingSource;
                    _grid.Columns["Selected"].DataPropertyName = "Selected";

                    _statusLabel.Text = $"Found {_rows.Count} .p7m attachment(s) (notes + email attachments).";
                    _btnConvert.Enabled = _rows.Count > 0;
                }
            });
        }

        // ---------- STEP 2: CONVERT ----------
        private void ConvertSelected()
        {
            _grid.EndEdit();

            var selected = _rows.FindAll(r => r.Selected);
            if (selected.Count == 0)
            {
                MessageBox.Show("Check at least one row to convert.", "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = selected.Count;
            _progressBar.Value = 0;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Converting attachments...",
                Work = (worker, args) =>
                {
                    int done = 0;
                    foreach (var row in selected)
                    {
                        byte[] rawBytes = null;
                        try
                        {
                            byte[] p7mBytes = Convert.FromBase64String(row.DocumentBody);
                            rawBytes = p7mBytes;

                            string fallbackName = row.FileName.EndsWith(".p7m", StringComparison.OrdinalIgnoreCase)
                                ? row.FileName.Substring(0, row.FileName.Length - 4)
                                : row.FileName + ".decoded";

                            // Handles binary PKCS#7, Base64/PEM text, and S/MIME
                            // messages (clear-signed multipart/signed or opaque).
                            var files = ExtractFiles(p7mBytes, fallbackName, 0);
                            if (files.Count == 0)
                                throw new InvalidOperationException("No attachment found inside the P7M content.");

                            foreach (var f in files)
                            {
                                var newNote = new Entity("annotation");
                                newNote["subject"] = "Converted from P7M: " + f.Name;
                                newNote["filename"] = f.Name;
                                newNote["documentbody"] = Convert.ToBase64String(f.Data);
                                newNote["mimetype"] = GuessMimeType(f.Name, f.ContentType);
                                if (row.RegardingRef != null)
                                    newNote["objectid"] = row.RegardingRef;
                                Service.Create(newNote);
                            }

                            row.Status = "Converted OK: " + string.Join(", ", files.ConvertAll(x => x.Name));
                        }
                        catch (Exception ex)
                        {
                            row.Status = "FAILED: " + ex.Message + " | v"
                                + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                                + " | " + Describe(rawBytes);
                        }

                        done++;
                        worker.ReportProgress(done);
                    }

                    args.Result = done;
                },
                ProgressChanged = (args) =>
                {
                    _progressBar.Value = Math.Min(args.ProgressPercentage, _progressBar.Maximum);
                    _bindingSource?.ResetBindings(false);
                },
                PostWorkCallBack = (args) =>
                {
                    _progressBar.Visible = false;
                    _bindingSource?.ResetBindings(false);

                    if (args.Error != null)
                    {
                        MessageBox.Show($"Error during conversion: {args.Error.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        _statusLabel.Text = "Error during conversion.";
                        return;
                    }

                    _statusLabel.Text = $"Conversion complete. {selected.Count} item(s) processed — check Status column.";
                }
            });
        }

        // ---------- HELPERS ----------

        private class ExtractedFile
        {
            public string Name;
            public string ContentType;
            public byte[] Data;
        }

        private static readonly System.Text.Encoding Latin1 = System.Text.Encoding.GetEncoding(28591);

        private static bool LooksLikeMime(byte[] data)
        {
            int n = Math.Min(512, data.Length);
            string head = Latin1.GetString(data, 0, n).TrimStart();
            return head.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("MIME-Version:", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("Content-Transfer-Encoding:", StringComparison.OrdinalIgnoreCase);
        }

        private static List<ExtractedFile> ExtractFiles(byte[] data, string fallbackName, int depth)
        {
            var results = new List<ExtractedFile>();
            if (depth > 5) return results;

            // S/MIME message stored as text (clear-signed multipart/signed, or
            // opaque application/pkcs7-mime with MIME headers).
            if (LooksLikeMime(data))
            {
                ExtractFromMime(Latin1.GetString(data), fallbackName, results, depth);
                return results;
            }

            // Binary PKCS#7 (possibly wrapped in Base64 text / PEM).
            byte[] der = NormalizeToDer(data);
            byte[] content;
            string contentType = null;
            try
            {
                var signedCms = new SignedCms();
                signedCms.Decode(der);
                content = signedCms.ContentInfo.Content;
                contentType = signedCms.ContentInfo.ContentType?.Value;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                var envelopedCms = new EnvelopedCms();
                envelopedCms.Decode(der);
                envelopedCms.Decrypt();
                content = envelopedCms.ContentInfo.Content;
                contentType = envelopedCms.ContentInfo.ContentType?.Value;
            }

            // The signed content of a real S/MIME message is itself a MIME entity.
            if (LooksLikeMime(content))
                return ExtractFiles(content, fallbackName, depth + 1);

            results.Add(new ExtractedFile { Name = fallbackName, ContentType = contentType, Data = content });
            return results;
        }

        private static void ExtractFromMime(string text, string fallbackName, List<ExtractedFile> results, int depth)
        {
            int sepLen = 4;
            int idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            int idx2 = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (idx < 0 || (idx2 >= 0 && idx2 < idx)) { idx = idx2; sepLen = 2; }
            if (idx < 0) return;

            string head = System.Text.RegularExpressions.Regex.Replace(text.Substring(0, idx), @"\r?\n[ \t]+", " ");
            string body = text.Substring(idx + sepLen);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in head.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int c = line.IndexOf(':');
                if (c > 0) headers[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
            }

            string ct = headers.ContainsKey("Content-Type") ? headers["Content-Type"] : "text/plain";
            string mediaType = ct.Split(';')[0].Trim().ToLowerInvariant();
            string cte = headers.ContainsKey("Content-Transfer-Encoding")
                ? headers["Content-Transfer-Encoding"].Trim().ToLowerInvariant() : "7bit";

            if (mediaType.StartsWith("multipart/"))
            {
                string boundary = GetParam(ct, "boundary");
                if (boundary == null) return;
                var parts = body.Split(new[] { "--" + boundary }, StringSplitOptions.None);
                for (int i = 1; i < parts.Length; i++)
                {
                    if (parts[i].StartsWith("--")) break; // closing delimiter
                    ExtractFromMime(parts[i].TrimStart('\r', '\n'), fallbackName, results, depth);
                }
                return;
            }

            if (mediaType.Contains("pkcs7-signature")) return; // detached signature, not content

            byte[] bytes = DecodeBody(body, cte);

            if (mediaType.Contains("pkcs7-mime"))
            {
                results.AddRange(ExtractFiles(bytes, fallbackName, depth + 1));
                return;
            }

            string cd = headers.ContainsKey("Content-Disposition") ? headers["Content-Disposition"] : null;
            string name = GetParam(cd, "filename") ?? GetParam(ct, "name");
            if (name == null) return; // message body text, not an attachment

            results.Add(new ExtractedFile { Name = name, ContentType = mediaType, Data = bytes });
        }

        private static string GetParam(string header, string param)
        {
            if (string.IsNullOrEmpty(header)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(header,
                @"(?<![A-Za-z0-9*])" + param + @"\s*=\s*(?:""([^""]*)""|([^;\s]+))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }

        private static byte[] DecodeBody(string body, string cte)
        {
            if (cte == "base64")
            {
                string clean = new string(Array.FindAll(body.ToCharArray(), c => !char.IsWhiteSpace(c)));
                return Convert.FromBase64String(clean);
            }
            if (cte == "quoted-printable")
            {
                var ms = new System.IO.MemoryStream();
                string s = body.Replace("=\r\n", "").Replace("=\n", "");
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == '=' && i + 2 < s.Length + 0 && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
                    {
                        ms.WriteByte(Convert.ToByte(s.Substring(i + 1, 2), 16));
                        i += 2;
                    }
                    else ms.WriteByte((byte)s[i]);
                }
                return ms.ToArray();
            }
            return Latin1.GetBytes(body);
        }

        // Shows length, first bytes (hex) and an ASCII preview, so a failing
        // file can be diagnosed without exposing its full contents.
        private static string Describe(byte[] b)
        {
            if (b == null) return "no data";
            int n = Math.Min(24, b.Length);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
                sb.Append((b[i] >= 32 && b[i] < 127) ? (char)b[i] : '.');
            return "len=" + b.Length + " hex=" + (n > 0 ? BitConverter.ToString(b, 0, n) : "") + " txt=" + sb;
        }

        // Turns whatever was stored into raw DER: handles raw DER, Base64 text,
        // PEM blocks, and full MIME messages (headers + Base64 body).
        private static byte[] NormalizeToDer(byte[] data)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                if (data.Length > 0 && data[0] == 0x30) return data;

                string text = System.Text.Encoding.ASCII.GetString(data);

                int begin = text.IndexOf("-----BEGIN", StringComparison.Ordinal);
                if (begin >= 0)
                {
                    int bodyStart = text.IndexOf('\n', begin);
                    int end = text.IndexOf("-----END", StringComparison.Ordinal);
                    if (bodyStart > 0 && end > bodyStart)
                        text = text.Substring(bodyStart + 1, end - bodyStart - 1);
                }
                else
                {
                    // MIME message: drop headers (everything up to first blank line).
                    int sep = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    int sepLen = 4;
                    if (sep < 0) { sep = text.IndexOf("\n\n", StringComparison.Ordinal); sepLen = 2; }
                    if (sep >= 0 && text.IndexOf(':') >= 0 && text.IndexOf(':') < sep)
                        text = text.Substring(sep + sepLen);
                }

                string clean = new string(Array.FindAll(text.ToCharArray(), c => !char.IsWhiteSpace(c)));
                try
                {
                    data = Convert.FromBase64String(clean);
                }
                catch (FormatException)
                {
                    return data;
                }
            }
            return data;
        }

        private static string GuessMimeType(string fileName, string innerContentType)
        {
            if (!string.IsNullOrEmpty(innerContentType))
                return innerContentType;

            var ext = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant();
            switch (ext)
            {
                case ".pdf": return "application/pdf";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".doc": return "application/msword";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".xls": return "application/vnd.ms-excel";
                case ".xml": return "application/xml";
                case ".txt": return "text/plain";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                default: return "application/octet-stream";
            }
        }
    }
}