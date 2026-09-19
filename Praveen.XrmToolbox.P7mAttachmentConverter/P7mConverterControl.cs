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
                AllowUserToAddRows = false,
                ReadOnly = true
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
                Width = 300
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "RegardingId",
                HeaderText = "Regarding Record",
                DataPropertyName = "RegardingId",
                Width = 260
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Status",
                HeaderText = "Status",
                DataPropertyName = "Status",
                Width = 200
            });

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
                    var query = new QueryExpression("annotation")
                    {
                        ColumnSet = new ColumnSet("filename", "documentbody", "objectid", "mimetype", "subject")
                    };
                    query.Criteria.AddCondition("filename", ConditionOperator.EndsWith, ".p7m");
                    query.TopCount = 500;

                    args.Result = Service.RetrieveMultiple(query);
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

                    var results = (EntityCollection)args.Result;
                    foreach (var e in results.Entities)
                    {
                        _rows.Add(new AttachmentRow
                        {
                            AnnotationId = e.Id,
                            FileName = e.GetAttributeValue<string>("filename"),
                            RegardingRef = e.GetAttributeValue<EntityReference>("objectid"),
                            DocumentBody = e.GetAttributeValue<string>("documentbody")
                        });
                    }

                    _bindingSource = new BindingSource { DataSource = _rows };
                    _grid.DataSource = _bindingSource;
                    _grid.Columns["Selected"].DataPropertyName = "Selected";

                    _statusLabel.Text = $"Found {_rows.Count} .p7m attachment(s).";
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
                        try
                        {
                            byte[] p7mBytes = Convert.FromBase64String(row.DocumentBody);
                            byte[] originalBytes;
                            string innerContentType = null;

                            try
                            {
                                var signedCms = new SignedCms();
                                signedCms.Decode(p7mBytes);
                                originalBytes = signedCms.ContentInfo.Content;
                                innerContentType = signedCms.ContentInfo.ContentType?.Value;
                            }
                            catch (System.Security.Cryptography.CryptographicException)
                            {
                                var envelopedCms = new EnvelopedCms();
                                envelopedCms.Decode(p7mBytes);
                                envelopedCms.Decrypt();
                                originalBytes = envelopedCms.ContentInfo.Content;
                                innerContentType = envelopedCms.ContentInfo.ContentType?.Value;
                            }

                            string originalFileName = row.FileName.EndsWith(".p7m", StringComparison.OrdinalIgnoreCase)
                                ? row.FileName.Substring(0, row.FileName.Length - 4)
                                : row.FileName + ".decoded";

                            var newNote = new Entity("annotation");
                            newNote["subject"] = "Converted from P7M: " + originalFileName;
                            newNote["filename"] = originalFileName;
                            newNote["documentbody"] = Convert.ToBase64String(originalBytes);
                            newNote["mimetype"] = GuessMimeType(originalFileName, innerContentType);
                            if (row.RegardingRef != null)
                                newNote["objectid"] = row.RegardingRef;

                            Service.Create(newNote);

                            row.Status = "Converted OK";
                        }
                        catch (Exception ex)
                        {
                            row.Status = "FAILED: " + ex.Message;
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
