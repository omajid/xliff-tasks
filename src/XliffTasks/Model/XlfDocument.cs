﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using XliffTasks.Tasks;

namespace XliffTasks.Model
{
    /// <summary>
    /// Represents a document in XLIFF format which can be updated from source from
    /// <see cref="TranslatableDocument"/> instances and produce translation data
    /// for <see cref="TranslatableDocument.Translate"/>.
    /// 
    /// See https://en.wikipedia.org/wiki/XLIFF
    /// </summary>
    internal sealed class XlfDocument : Document
    {
        private XDocument _document;

        /// <summary>
        /// Indicates if content has been loaded in to the document.
        /// </summary>
        public override bool HasContent => _document != null;

        /// <summary>
        /// Loads (or reloads) the document content from the given reader.
        /// </summary>
        public override void Load(TextReader reader)
        {
            _document = XDocument.Load(reader);
        }

        /// <summary>
        /// Loads initial document content for a new XLIFF document.
        /// </summary>
        public void LoadNew(string targetLanguage)
        {
            XNamespace ns = "urn:oasis:names:tc:xliff:document:1.2";
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

            _document = new XDocument(
                new XElement(ns + "xliff",
                    new XAttribute("xmlns", ns.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
                    new XAttribute("version", "1.2"),
                    new XAttribute(xsi + "schemaLocation", $"{ns.NamespaceName} xliff-core-1.2-transitional.xsd"),
                    new XElement(ns + "file",
                        new XAttribute("datatype", "xml"),
                        new XAttribute("source-language", "en"),
                        new XAttribute("target-language", targetLanguage),
                        new XAttribute("original", "_"), // placeholder will be replaced on first update
                        new XElement(ns + "body"))));
        }

        /// <summary>
        /// Saves the document's content (with translations applied if <see cref="Translate" /> was called) to the given file path.
        /// </summary>
        public override void Save(TextWriter writer)
        {
            EnsureContent();
            _document.SaveCustom(writer);
        }

        /// <summary>
        /// Updates this XLIFF document with the source data from the given translatable document.
        /// </summary>
        /// <returns>True if any changes were made to this document.</returns>
        public bool Update(TranslatableDocument sourceDocument, string sourceDocumentId)
        {
            bool changed = false;
            var nodesById = new Dictionary<string, TranslatableNode>();
            foreach (var node in sourceDocument.Nodes)
            {
                if (nodesById.ContainsKey(node.Id))
                {
                    throw new BuildErrorException($"The document '{sourceDocumentId}' has a duplicate node '{node.Id}'.");
                }

                nodesById.Add(node.Id, node);
            }

            XNamespace ns = _document.Root.Name.Namespace;

            XElement fileElement = _document.Root.Element(ns + "file");
            XAttribute originalAttribute = fileElement.Attribute("original");
            if (originalAttribute.Value != sourceDocumentId)
            {
                // update original path in case where user has renaned source file and corresponding xlf
                originalAttribute.Value = sourceDocumentId;
                changed = true;
            }

            XElement bodyElement = fileElement.Element(ns + "body");
            XElement groupElement = bodyElement.Element(ns + "group");

            if (groupElement != null && !groupElement.Elements().Any())
            {
                // remove unnecessary empty group added by older tool. We don't want to bother keeping that unnecessary id up-to-date.
                groupElement.Remove();
                changed = true;
            }

            foreach (XElement unitElement in bodyElement.Descendants(ns + "trans-unit").ToList())
            {
                XElement sourceElement = unitElement.Element(ns + "source");
                XElement targetElement = unitElement.Element(ns + "target");
                XElement noteElement = unitElement.Element(ns + "note");
                XAttribute idAttribute = unitElement.Attribute("id");
                XAttribute stateAttribute = targetElement.Attribute("state");

                string id = idAttribute.Value;
                string state = stateAttribute.Value;
                string source = sourceElement.Value;
                string note = noteElement.Value;

                // delete node in document that has been removed from source.
                if (!nodesById.TryGetValue(id, out TranslatableNode sourceNode))
                {
                    unitElement.Remove();
                    changed = true;
                    continue;
                }

                // update trans-unit state if either the source text or associated note has change.
                if (source != sourceNode.Source || (sourceNode.Note != null && note != sourceNode.Note))
                {
                    sourceElement.Value = sourceNode.Source;

                    // if sourceNode.Note is null, it indicates that the source format can't have notes, in which case
                    // they may be applied directly to the xlf by the user and we should not revert that on update
                    if (sourceNode.Note != null)
                    {
                        noteElement.Value = sourceNode.Note;
                        noteElement.SelfCloseIfPossible();
                    }

                    switch (state)
                    {
                        case "new":
                            // when a new string gets modified before it has been translated,
                            // update untranslated target to match the new source
                            targetElement.Value = sourceNode.Source;
                            break;

                        case "translated":
                            // flag strings that have been modified after translation for review/re-translation
                            stateAttribute.Value = "needs-review-translation";
                            break;
                    }

                    changed = true;
                }

                // If the source and target require different numbers of formatting items then reset
                // the target string completely. This avoids problems when the source has been updated
                // to remove formatting items--when formatting the target string we won't have as many
                // replacement items as it calls for, leading to an exception.
                // And if the source string is updated to use _more_ items then formatting with the
                // target string is likely to produce misleading (or outright meaningless) text. In
                // either case we lose nothing by just reverting the string until it can be localized
                // again.
                // Note we don't limit this check to when the source has changed in the original
                // document because we also want to catch errors introduced during translation.
                var sourceReplacementCount = sourceElement.Value.GetReplacementCount();
                var targetReplacementCount = targetElement.Value.GetReplacementCount();

                if (targetReplacementCount != sourceReplacementCount)
                {
                    targetElement.Value = sourceNode.Source;
                    stateAttribute.Value = "new";

                    changed = true;
                }

                // signal to loop below that this node is not new
                nodesById.Remove(id);
            }

            // Add new trans-units
            foreach (TranslatableNode sourceNode in sourceDocument.Nodes)
            {
                // Nodes that have been removed from nodesById table are not new and have already been handled.
                // Do not refactor this check away by iterating over dictionary values as the document order must be maintained deterministically.
                if (!nodesById.ContainsKey(sourceNode.Id))
                {
                    continue;
                }

                XElement newTransUnit = 
                    new XElement(ns + "trans-unit",
                        new XAttribute("id", sourceNode.Id),
                        new XElement(ns + "source", sourceNode.Source),
                        new XElement(ns + "target", new XAttribute("state", "new"), sourceNode.Source),
                        new XElement(ns + "note", sourceNode.Note == "" ? null : sourceNode.Note));

                bool inserted = false;
                foreach (var transUnit in bodyElement.Elements(ns + "trans-unit"))
                {
                    if (StringComparer.Ordinal.Compare(newTransUnit.Attribute("id").Value, transUnit.Attribute("id").Value) < 0)
                    {
                        transUnit.AddBeforeSelf(newTransUnit);
                        inserted = true;
                        break;
                    }
                }

                if (!inserted)
                {
                    bodyElement.Add(newTransUnit);
                }

                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Sorts the <code>trans-unit</code> elements in the document by their <code>id</code> attribute.
        /// </summary>
        /// <returns>Returns <code>true</code> if the document was modified; <code>false</code> otherwise.</returns>
        public bool Sort()
        {
            bool changed = false;

            XNamespace ns = _document.Root.Name.Namespace;

            XElement fileElement = _document.Root.Element(ns + "file");
            XElement bodyElement = fileElement.Element(ns + "body");

            IEnumerable<XElement> transUnits = bodyElement.Elements(ns + "trans-unit");

            IComparer<string> comparer = StringComparer.Ordinal;
            if (!transUnits.IsSorted(tu => tu.Attribute("id").Value, comparer))
            {
                changed = true;
                SortedList<string, XElement> sortedTransUnits = new SortedList<string, XElement>(comparer);

                // Sort the translation units
                foreach (var transUnit in transUnits)
                {
                    sortedTransUnits.Add(transUnit.Attribute("id").Value, transUnit);
                }

                // Remove them from the body element
                foreach (var transUnit in sortedTransUnits.Values)
                {
                    transUnit.Remove();
                }

                // Add them back in sorted order
                bodyElement.Add(sortedTransUnits.Values);
            }

            return changed;
        }

        /// <summary>
        /// Gets the translations (key=id, value=target), which can
        /// be passed on to <see cref="TranslatableDocument.Translate"/>.
        /// </summary>
        public IReadOnlyDictionary<string, string> GetTranslations()
        {
            var dictionary = new Dictionary<string, string>();
            XNamespace ns = _document.Root.Name.Namespace;

            foreach (var element in _document.Descendants(ns + "trans-unit"))
            {
                string id = element.Attribute("id").Value;
                string target = element.Element(ns + "target").Value;

                dictionary.Add(id, target);
            }

            return dictionary;
        }

        public ISet<string> GetUntranslatedResourceIDs()
        {
            XNamespace ns = _document.Root.Name.Namespace;

            var untranslatedResourceIDs =
                (_document.Descendants(ns + "trans-unit")
                 .Where(tu =>
                 {
                     var target = tu.Element(ns + "target");
                     return target.Attribute("state").Value != "translated";
                 })
                 .Select(tu => tu.Attribute("id").Value));

            return new HashSet<string>(untranslatedResourceIDs, StringComparer.Ordinal);
        }
    }
}