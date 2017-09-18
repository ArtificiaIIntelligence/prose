﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CsvHelper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.ProgramSynthesis.AST;
using Microsoft.ProgramSynthesis.Split.Text;
using Microsoft.ProgramSynthesis.Split.Text.Semantics;
using Microsoft.ProgramSynthesis.Split.Text.Translation.Python;
using Microsoft.ProgramSynthesis.Transformation.Text;
using Microsoft.ProgramSynthesis.Transformation.Text.Constraints;
using Microsoft.ProgramSynthesis.Transformation.Text.Translation.Python;
using Microsoft.ProgramSynthesis.Utils;
using ProseDemo.Web.Models;
using TTExample = Microsoft.ProgramSynthesis.Transformation.Text.Example;
using TTSession = Microsoft.ProgramSynthesis.Transformation.Text.Session;
using STSession = Microsoft.ProgramSynthesis.Split.Text.SplitSession;

namespace ProseDemo.Web.Controllers {
    public class HomeController : Controller {
        private const string DataKey = "Data_";
        private readonly IMemoryCache _cache;
        public HomeController(IMemoryCache cache) => _cache = cache;

        public IActionResult Index() => View();

        public IActionResult UploadData([FromForm] int dataLimit) {
            IFormFileCollection files = HttpContext.Request.Form.Files;
            if (files.Count != 1) return BadRequest($"Expected a single-file CSV upload, but got {files.Count} files");
            using (Stream stream = files[0].OpenReadStream()) {
                List<string[]> data = LoadData(stream, dataLimit);
                Guid dataId = Guid.NewGuid();
                _cache.Set(DataKey + dataId, data, new MemoryCacheEntryOptions()
                               .SetPriority(CacheItemPriority.NeverRemove)
                               .SetSlidingExpiration(TimeSpan.FromHours(1)));
                HttpContext.Session.SetString(DataKey, dataId.ToString());
                return Ok();
            }
        }

        [HttpPost]
        public async Task<IActionResult> TextTransformation([FromBody] TextTransformationRequest request) {
            if (!_cache.TryGetValue(DataKey + HttpContext.Session.GetString(DataKey), out List<string[]> data))
                return BadRequest("No data in the session. Please upload your dataset to the server first.");
            TTextLearnResponse output = await TextTransformation(data, request);
            if (output == null)
                return BadRequest("No program learned");
            return Json(output);
        }

        [HttpPost]
        public IActionResult SplitText([FromBody] SplitTextRequest request) {
            if (!_cache.TryGetValue(DataKey + HttpContext.Session.GetString(DataKey), out List<string[]> data))
                return BadRequest("No data in the session. Please upload your dataset to the server first.");
            if (request.SourceColumn == null)
                return BadRequest("Source column not specified.");
            STextLearnResponse output = 
                SplitText(data.Select(r => r[request.SourceColumn.Value]).ToArray(), request.Examples);
            if (output == null)
                return BadRequest("No program learned");
            return Json(output);
        }

        #region Learning

        public List<string[]> LoadData(Stream stream, int limit = int.MaxValue) {
            var data = new List<string[]>();
            using (var reader = new StreamReader(stream))
            using (var csv = new CsvReader(reader)) {
                while (csv.Read()) data.Add(csv.CurrentRecord);
            }
            return data.DeterministicallySample(limit).ToList();
        }

        public async Task<TTextLearnResponse> TextTransformation(List<string[]> data, TextTransformationRequest request) {
            using (var session = new TTSession()) {
                InputRow[] rows = data.Select(r => new InputRow(r)).ToArray();
                session.AddInputs(rows.Take(100));
                IEnumerable<TTExample> constraints = 
                    request.Examples.Select(e => new TTExample(rows[e.Row], e.Output));
                session.AddConstraints(constraints);

                if (request.SourceColumn != null) {
                    session.AddConstraints(new ColumnPriority(new[] {
                        new[] { request.SourceColumn.Value.ToString() },
                        rows[0].ColumnNames
                    }));
                }
                session.UseInputsInLearn = false;


                var program = session.Learn();
                var significantInputs = await session.GetSignificantInputsAsync();
                var siIndices = new List<int>();
                for (var i = 0; i < rows.Length; i++) {
                    if (significantInputs.Any(si => si.Input.Equals(rows[i])))
                        siIndices.Add(i);
                }
                
                if (program == null) return null;
                return new TTextLearnResponse {
                    Output = rows.Select(program.Run).ToArray(),
                    SignificantInputs = siIndices,
                    Description = program.Describe(),
                    ProgramHumanReadable = program.Serialize(ASTSerializationFormat.HumanReadable),
                    ProgramXML = program.Serialize(ASTSerializationFormat.XML),
                    ProgramPython = program.ToPython()
                };
            }
        }

        public STextLearnResponse SplitText(string[] column, Dictionary<int, string[]> examples) {
            using (var session = new STSession()) {
                session.AddInputs(column.Select(STSession.CreateStringRegion));
                session.AddConstraints(new IncludeDelimitersInOutput(false));
                foreach (var kvp in examples) {
                    for (var i = 0; i < kvp.Value.Length; i++) {
                        session.AddConstraints(new NthExampleConstraint(column[kvp.Key], i, kvp.Value[i]));
                    }
                }

                IEnumerable<SplitCell[]> output = session.LearnOutputs();
                SplitProgram program = session.Learn(); // cached
                return new STextLearnResponse {
                    Output = output?.Select(r => r.Select(c => c?.CellValue?.Value).ToArray()).ToList(),
                    Description = program.Describe(),
                    ProgramHumanReadable = program.Serialize(ASTSerializationFormat.HumanReadable),
                    ProgramXML = program.Serialize(ASTSerializationFormat.XML),
                    ProgramPython = program.ToPython()
                };
            }
        }

        #endregion
    }
}