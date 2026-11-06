using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace FacadeJoin2024
{
    [Transaction(TransactionMode.Manual)]
    public class JoinCommand : IExternalCommand
    {
        sealed class Filter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Group || IsGeneric(e);
            public bool AllowReference(Reference r, XYZ p) => false;
        }
        static bool IsGeneric(Element e) => e != null && !(e is ElementType) &&
            e.Category != null && e.Category.Id.Value == (long)BuiltInCategory.OST_GenericModel;

        sealed class Item
        {
            public Element Element;
            public XYZ Min, Max;
        }

        // Reject all failure messages, including warnings: a join must commit cleanly.
        sealed class Failures : IFailuresPreprocessor
        {
            public readonly List<string> Messages = new List<string>();
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (var f in a.GetFailureMessages()) Messages.Add(f.GetDescriptionText());
                return Messages.Count == 0 ? FailureProcessingResult.Continue : FailureProcessingResult.ProceedWithRollBack;
            }
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var ui = data.Application.ActiveUIDocument;
            if (ui == null || ui.Document.IsFamilyDocument)
            {
                message = "Откройте проект Revit, а не редактор семейства.";
                return Result.Failed;
            }
            var doc = ui.Document;
            var log = new List<string> { "FacadeJoin2024 | " + DateTime.Now.ToString("s"), "Проект: " + doc.Title };
            try
            {
                var ids = ui.Selection.GetElementIds().ToList();
                if (ids.Count == 0)
                    ids = ui.Selection.PickObjects(ObjectType.Element, new Filter(),
                        "Выберите обобщённые модели или группы фасада. Завершить — Готово.")
                        .Select(r => r.ElementId).ToList();
                var visited = new HashSet<long>();
                var chosen = new List<Element>();
                Action<ElementId> collect = null;
                collect = id =>
                {
                    if (!visited.Add(id.Value)) return;
                    var e = doc.GetElement(id);
                    if (e is Group g) foreach (var member in g.GetMemberIds()) collect(member);
                    else if (IsGeneric(e)) chosen.Add(e);
                };
                foreach (var id in ids) collect(id);
                var items = new List<Item>();
                foreach (var e in chosen)
                {
                    var b = e.get_BoundingBox(null);
                    if (b == null) { log.Add("Без габаритов: " + e.Id.Value); continue; }
                    var corners = new List<XYZ>();
                    for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
                        corners.Add(b.Transform.OfPoint(new XYZ(x == 0 ? b.Min.X : b.Max.X,
                            y == 0 ? b.Min.Y : b.Max.Y, z == 0 ? b.Min.Z : b.Max.Z)));
                    items.Add(new Item { Element = e,
                        Min = new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z)),
                        Max = new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z)) });
                }
                if (items.Count < 2)
                {
                    TaskDialog.Show("Соединение фасада", "Выделите минимум две обобщённые модели или группы с ними и повторите команду.");
                    return Result.Cancelled;
                }
                items = items.OrderBy(i => i.Min.X).ThenBy(i => i.Element.Id.Value).ToList();
                const double tolerance = 0.1 / 304.8; // Only candidate search; never moves geometry.
                var pairs = new List<Tuple<Element, Element>>();
                for (int i = 0; i < items.Count; i++)
                for (int j = i + 1; j < items.Count; j++)
                {
                    var a = items[i]; var b = items[j];
                    if (b.Min.X > a.Max.X + tolerance) break;
                    if (b.Min.Y > a.Max.Y + tolerance || a.Min.Y > b.Max.Y + tolerance ||
                        b.Min.Z > a.Max.Z + tolerance || a.Min.Z > b.Max.Z + tolerance) continue;
                    pairs.Add(Tuple.Create(a.Element, b.Element));
                    if (pairs.Count > 20000)
                    {
                        TaskDialog.Show("Соединение фасада", "Более 20 000 соседних пар. Выделите меньший участок фасада. Изменений нет.");
                        return Result.Cancelled;
                    }
                }
                int joined = 0, existing = 0, failed = 0;
                using (var batch = new TransactionGroup(doc, "Соединить обобщённые модели фасада"))
                {
                    batch.Start();
                    foreach (var pair in pairs)
                    {
                        var a = pair.Item1; var b = pair.Item2;
                        string label = a.Id.Value + " [группа " + a.GroupId.Value + "] + " + b.Id.Value + " [группа " + b.GroupId.Value + "]";
                        if (JoinGeometryUtils.AreElementsJoined(doc, a, b))
                        { existing++; log.Add("Уже соединены: " + label); continue; }
                        // Separate transactions isolate deferred group/geometry failures at commit.
                        using (var tx = new Transaction(doc, "Соединение пары фасада"))
                        {
                            tx.Start();
                            var failures = new Failures();
                            tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions()
                                .SetFailuresPreprocessor(failures).SetClearAfterRollback(true)
                                .SetForcedModalHandling(true));
                            try
                            {
                                JoinGeometryUtils.JoinGeometry(doc, a, b);
                                var status = tx.Commit();
                                if (status == TransactionStatus.Committed)
                                { joined++; log.Add("Соединены: " + label); }
                                else
                                { failed++; log.Add("Откат: " + label + " | " + string.Join("; ", failures.Messages)); }
                            }
                            catch (Exception ex)
                            {
                                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                                failed++; log.Add("Не соединены: " + label + " | " + ex.Message);
                            }
                        }
                    }
                    if (batch.Assimilate() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Не удалось завершить группу транзакций. Соединения не подтверждены.");
                }
                var summary = "Обобщённых моделей: " + chosen.Count + "\nСоседних пар: " + pairs.Count +
                    "\nСоединено: " + joined + "\nУже соединены: " + existing + "\nНе удалось: " + failed +
                    "\n\nГруппы не разгруппировывались. Отмена изменений — Ctrl+Z.";
                log.Add(summary);
                string report;
                try
                {
                    var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "A101", "FacadeJoin2024", "Reports");
                    Directory.CreateDirectory(folder);
                    report = Path.Combine(folder, "Join-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".txt");
                    File.WriteAllLines(report, log, Encoding.UTF8);
                    report = "\n\nОтчёт: " + report;
                }
                catch (Exception ex) { report = "\n\nНе удалось записать отчёт: " + ex.Message; }
                var dialog = new TaskDialog("Соединение фасада") { MainInstruction = "Обработка завершена", MainContent = summary + report,
                    ExpandedContent = string.Join("\n", log.Take(100)), CommonButtons = TaskDialogCommonButtons.Close };
                dialog.Show();
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex) { message = ex.ToString(); return Result.Failed; }
        }
    }
}
