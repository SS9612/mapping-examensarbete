import { useEffect, useState, useMemo, useCallback } from "react";
import * as XLSX from "xlsx";
import {
  getInitialBatch,
  approveCompetence,
  rejectCompetence,
  assignToOther,
  getMetadata,
  updateCategorization,
  getCompetence,
  getAllApproved,
  bulkApprove,
  bulkMarkImported,
  bulkMoveToPending,
  bulkDelete,
  bulkReject,
  getCounts,
  deleteCompetence,
} from "../api/reviewApi";
import { mapSingle } from "../api/areaMapperApi";
import { SkeletonCard, SkeletonTable } from "../components/SkeletonLoader";
import { showError, showSuccess, getErrorMessage } from "../utils/errorHandler";
import { parseErrorMessage } from "../utils/mappingErrorParser";

const TABS = ["pending", "approved", "rejected", "legacy", "imported", "archive"];

function formatDate(dateString) {
  if (!dateString) return "—";
  const date = new Date(dateString);
  const now = new Date();
  const diffMs = now - date;
  const diffMins = Math.floor(diffMs / 60000);
  const diffHours = Math.floor(diffMs / 3600000);
  const diffDays = Math.floor(diffMs / 86400000);

  if (diffMins < 1) return "Just now";
  if (diffMins < 60) return `${diffMins}m ago`;
  if (diffHours < 24) return `${diffHours}h ago`;
  if (diffDays < 7) return `${diffDays}d ago`;

  return date.toLocaleDateString("en-US", { month: "short", day: "numeric", year: date.getFullYear() !== now.getFullYear() ? "numeric" : undefined });
}

function formatDateFull(dateString) {
  if (!dateString) return "—";
  const date = new Date(dateString);
  return date.toLocaleString("en-US", {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function ConfidenceBadge({ confidence }) {
  if (confidence == null) return <span className="muted">—</span>;
  
  const level = confidence >= 0.7 ? "high" : confidence >= 0.4 ? "medium" : "low";
  const percentage = Math.round(confidence * 100);
  
  return (
    <div className="confidence-badge">
      <div className={`confidence-level confidence-${level}`}>
        <span className="confidence-value">{percentage}%</span>
        <div className="confidence-bar">
          <div 
            className={`confidence-fill confidence-fill-${level}`}
            style={{ width: `${percentage}%` }}
          />
        </div>
      </div>
    </div>
  );
}

export default function ReviewPage() {
  const [status, setStatus] = useState(() => {
    return localStorage.getItem("reviewStatus") || "pending";
  });
  const [items, setItems] = useState([]);
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(() => {
    const stored = localStorage.getItem("reviewPageSize");
    return stored ? Number(stored) : 10;
  });
  const [selectedNote, setSelectedNote] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [sortField, setSortField] = useState(() => {
    return localStorage.getItem("reviewSortField") || "createdAt";
  });
  const [sortDirection, setSortDirection] = useState(() => {
    return localStorage.getItem("reviewSortDirection") || "desc";
  });
  const [expandedRows, setExpandedRows] = useState(new Set());
  const [exportLoading, setExportLoading] = useState(false);
  const [searchQuery, setSearchQuery] = useState("");
  const [filters, setFilters] = useState({
    area: "",
    category: "",
    subcategory: "",
    matchedType: "",
  });
  const [rejectModalItem, setRejectModalItem] = useState(null);
  const [rejectNotes, setRejectNotes] = useState("");
  const [rejectSubmitting, setRejectSubmitting] = useState(false);
  const [categorizationModalItem, setCategorizationModalItem] = useState(null);
  const [metadata, setMetadata] = useState(null);
  const [categorizationLoading, setCategorizationLoading] = useState(false);
  const [categorizationSubmitting, setCategorizationSubmitting] = useState(false);
  const [selectedAreaId, setSelectedAreaId] = useState(null);
  const [selectedCategoryId, setSelectedCategoryId] = useState(null);
  const [selectedSubcategoryId, setSelectedSubcategoryId] = useState(null);
  const [editedCompetenceName, setEditedCompetenceName] = useState("");
  const [selectedItems, setSelectedItems] = useState(new Set());
  const [bulkActionLoading, setBulkActionLoading] = useState(false);
  const [totalCounts, setTotalCounts] = useState({
    pending: 0,
    approved: 0,
    rejected: 0,
    legacy: 0,
    imported: 0,
    archive: 0,
  });

  const load = useCallback(async (staleRef) => {
    setLoading(true);
    setError("");
    try {
      const data = await getInitialBatch(status);
      if (staleRef?.stale) return;
      setItems(data || []);
    } catch (err) {
      if (staleRef?.stale) return;
      const errorMsg = "Failed to load competences";
      setError(errorMsg);
      showError(err, errorMsg);
    } finally {
      if (!staleRef?.stale) setLoading(false);
    }
  }, [status]);

  useEffect(() => {
    setPage(0);
    setExpandedRows(new Set());
    setSelectedItems(new Set());
    localStorage.setItem("reviewStatus", status);
    const ref = { stale: false };
    load(ref);
    return () => { ref.stale = true; };
  }, [status, load]);

  useEffect(() => {
    localStorage.setItem("reviewPageSize", String(pageSize));
  }, [pageSize]);

  useEffect(() => {
    localStorage.setItem("reviewSortField", sortField);
    localStorage.setItem("reviewSortDirection", sortDirection);
  }, [sortField, sortDirection]);

  // Reset to page 0 when filters or search change
  useEffect(() => {
    setPage(0);
    setSelectedItems(new Set());
  }, [searchQuery, filters.area, filters.category, filters.subcategory, filters.matchedType]);

  const filteredAndSortedItems = useMemo(() => {
    const q = searchQuery.trim().toLowerCase();

    const filtered = items.filter(item => {
      const matchesSearch =
        !q ||
        (item.name || "").toLowerCase().includes(q) ||
        (item.normalized || "").toLowerCase().includes(q);

      const matchesArea = !filters.area || item.areaName === filters.area;
      const matchesCategory = !filters.category || item.categoryName === filters.category;
      const matchesSubcategory = !filters.subcategory || item.subcategoryName === filters.subcategory;
      const matchesMatchedType = !filters.matchedType || item.matchedType === filters.matchedType;

      return matchesSearch && matchesArea && matchesCategory && matchesSubcategory && matchesMatchedType;
    });

    const sorted = [...filtered];
    sorted.sort((a, b) => {
      let aVal, bVal;
      
      switch (sortField) {
        case "name":
          aVal = (a.name || "").toLowerCase();
          bVal = (b.name || "").toLowerCase();
          break;
        case "confidence":
          aVal = a.confidence ?? 0;
          bVal = b.confidence ?? 0;
          break;
        case "createdAt":
          aVal = a.createdAt ? new Date(a.createdAt).getTime() : 0;
          bVal = b.createdAt ? new Date(b.createdAt).getTime() : 0;
          break;
        case "reviewedAt":
          aVal = a.reviewedAt ? new Date(a.reviewedAt).getTime() : 0;
          bVal = b.reviewedAt ? new Date(b.reviewedAt).getTime() : 0;
          break;
        case "area":
          aVal = (a.areaName || "").toLowerCase();
          bVal = (b.areaName || "").toLowerCase();
          break;
        default:
          return 0;
      }
      
      if (aVal < bVal) return sortDirection === "asc" ? -1 : 1;
      if (aVal > bVal) return sortDirection === "asc" ? 1 : -1;
      return 0;
    });
    
    return sorted;
  }, [items, sortField, sortDirection, searchQuery, filters]);

  // Paginate the filtered and sorted items
  const paginatedItems = useMemo(() => {
    const start = page * pageSize;
    const end = start + pageSize;
    return filteredAndSortedItems.slice(start, end);
  }, [filteredAndSortedItems, page, pageSize]);

  // Calculate hasMore based on filtered/sorted items
  const hasMore = useMemo(() => {
    const start = page * pageSize;
    return start + pageSize < filteredAndSortedItems.length;
  }, [filteredAndSortedItems.length, page, pageSize]);

  const uniqueFilterValues = useMemo(() => {
    const areas = new Set();
    const categories = new Set();
    const subcategories = new Set();
    const matchedTypes = new Set();

    items.forEach(i => {
      if (i.areaName) areas.add(i.areaName);
      if (i.categoryName) categories.add(i.categoryName);
      if (i.subcategoryName) subcategories.add(i.subcategoryName);
      if (i.matchedType) matchedTypes.add(i.matchedType);
    });

    return {
      areas: Array.from(areas).sort(),
      categories: Array.from(categories).sort(),
      subcategories: Array.from(subcategories).sort(),
      matchedTypes: Array.from(matchedTypes).sort(),
    };
  }, [items]);

  // Load accurate aggregate counts for KPIs once on mount
  useEffect(() => {
    async function loadCounts() {
      try {
        const counts = await getCounts();
        setTotalCounts({
          pending: counts.pending ?? 0,
          approved: counts.approved ?? 0,
          rejected: counts.rejected ?? 0,
          legacy: counts.legacy ?? 0,
          imported: counts.imported ?? 0,
          archive: counts.archive ?? 0,
        });
      } catch (err) {
        console.error("Failed to load total counts:", err);
      }
    }
    loadCounts();
  }, []);

  // Total counts from all statuses
  const totalPendingCount = totalCounts.pending;
  const totalApprovedCount = totalCounts.approved;
  const totalRejectedCount = totalCounts.rejected;
  const totalArchiveCount = totalCounts.archive;

  // Allow selection in both Pending and Approved tabs
  const selectionEnabled = status === "pending" || status === "approved";

  const tableColumnCount =
    (selectionEnabled ? 1 : 0) +
    10 + // base columns: Name, Normalized, Area, Category, Subcategory, Matched Type, Confidence, Created, Notes, Actions
    (status === "approved" || status === "rejected" ? 1 : 0); // Reviewed column
  
  // Statistics
  const averageConfidence = useMemo(() => {
    if (!filteredAndSortedItems.length) return null;
    const vals = filteredAndSortedItems
      .map(i => i.confidence)
      .filter(c => c != null);
    if (!vals.length) return null;
    const sum = vals.reduce((acc, c) => acc + c, 0);
    return sum / vals.length;
  }, [filteredAndSortedItems]);

  const itemsReviewedToday = useMemo(() => {
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    return items.filter(item => {
      if (!item.reviewedAt) return false;
      const reviewedDate = new Date(item.reviewedAt);
      reviewedDate.setHours(0, 0, 0, 0);
      return reviewedDate.getTime() === today.getTime();
    }).length;
  }, [items]);

  const topAreas = useMemo(() => {
    const areaCounts = new Map();
    items.forEach(item => {
      if (item.areaName) {
        areaCounts.set(item.areaName, (areaCounts.get(item.areaName) || 0) + 1);
      }
    });
    return Array.from(areaCounts.entries())
      .sort((a, b) => b[1] - a[1])
      .slice(0, 3)
      .map(([name, count]) => ({ name, count }));
  }, [items]);

  const lowConfidenceCount = useMemo(() => {
    return items.filter(item => item.confidence != null && item.confidence < 0.4).length;
  }, [items]);

  function handleSort(field) {
    if (sortField === field) {
      setSortDirection(sortDirection === "asc" ? "desc" : "asc");
    } else {
      setSortField(field);
      setSortDirection("asc");
    }
  }

  function clampPageAfterRemoval(removedCount) {
    setPage(prev => {
      const remaining = filteredAndSortedItems.length - removedCount;
      const maxPage = Math.max(0, Math.ceil(remaining / pageSize) - 1);
      return Math.min(prev, maxPage);
    });
  }

  function toggleRow(competenceId) {
    const newExpanded = new Set(expandedRows);
    if (newExpanded.has(competenceId)) {
      newExpanded.delete(competenceId);
    } else {
      newExpanded.add(competenceId);
    }
    setExpandedRows(newExpanded);
  }

  async function handleApprove(item) {
    try {
      await approveCompetence(item.competenceId, "Approved via UI");
      showSuccess("Competence approved successfully");
      setItems(prev => prev.filter(i => i.competenceId !== item.competenceId));
      setSelectedItems(prev => {
        const next = new Set(prev);
        next.delete(item.competenceId);
        return next;
      });
      setTotalCounts(prev => ({
        ...prev,
        pending: Math.max(0, prev.pending - 1),
        approved: prev.approved + 1
      }));
      clampPageAfterRemoval(1);
    } catch (err) {
      showError(err, "Failed to approve competence");
    }
  }

  function openRejectModal(item) {
    setRejectModalItem(item);
    setRejectNotes("");
  }

  function closeRejectModal() {
    if (rejectSubmitting) return;
    setRejectModalItem(null);
    setRejectNotes("");
  }

  async function confirmReject() {
    if (!rejectModalItem || !rejectNotes.trim()) return;

    // Check if this is a bulk reject
    if (rejectModalItem.competenceId === null && selectedItems.size > 0) {
      await confirmBulkReject();
      return;
    }

    setRejectSubmitting(true);
    try {
      await rejectCompetence(rejectModalItem.competenceId, rejectNotes.trim());
      showSuccess("Competence rejected successfully");
      setItems(prev => prev.filter(i => i.competenceId !== rejectModalItem.competenceId));
      setSelectedItems(prev => {
        const next = new Set(prev);
        next.delete(rejectModalItem.competenceId);
        return next;
      });
      setTotalCounts(prev => ({
        ...prev,
        pending: Math.max(0, prev.pending - 1),
        rejected: prev.rejected + 1
      }));
      clampPageAfterRemoval(1);
      closeRejectModal();
    } catch (err) {
      showError(err, "Failed to reject competence");
    } finally {
      setRejectSubmitting(false);
    }
  }

  async function handleAssignOther(item) {
    try {
      await assignToOther(item.competenceId, "Assigned to Other via UI");
      showSuccess("Competence assigned to 'Other' area successfully");
      setItems(prev => prev.filter(i => i.competenceId !== item.competenceId));
      setSelectedItems(prev => {
        const next = new Set(prev);
        next.delete(item.competenceId);
        return next;
      });
      setTotalCounts(prev => ({
        ...prev,
        pending: Math.max(0, prev.pending - 1),
        approved: prev.approved + 1
      }));
      clampPageAfterRemoval(1);
    } catch (err) {
      showError(err, "Failed to assign competence to Other");
    }
  }

  async function handleDelete(item) {
    if (
      !window.confirm(
        `Delete competence "${item.name}"?\n\nThis will remove it from the review list so it can be mapped again later.`
      )
    ) {
      return;
    }

    try {
      await deleteCompetence(item.competenceId);
      showSuccess("Competence deleted successfully");

      setItems(prev => prev.filter(i => i.competenceId !== item.competenceId));

      setSelectedItems(prev => {
        const next = new Set(prev);
        next.delete(item.competenceId);
        return next;
      });

      setExpandedRows(prev => {
        const next = new Set(prev);
        next.delete(item.competenceId);
        return next;
      });

      setTotalCounts(prev => {
        const updated = { ...prev };

        if (status === "pending") {
          updated.pending = Math.max(0, prev.pending - 1);
        } else if (status === "approved") {
          updated.approved = Math.max(0, prev.approved - 1);
        } else if (status === "rejected") {
          updated.rejected = Math.max(0, prev.rejected - 1);
        } else if (status === "legacy") {
          updated.legacy = Math.max(0, prev.legacy - 1);
          updated.archive = Math.max(0, prev.archive - 1);
        } else if (status === "imported") {
          updated.imported = Math.max(0, prev.imported - 1);
          updated.archive = Math.max(0, prev.archive - 1);
        } else if (status === "archive") {
          updated.archive = Math.max(0, prev.archive - 1);
        }

        return updated;
      });

      clampPageAfterRemoval(1);
    } catch (err) {
      showError(err, "Failed to delete competence");
    }
  }

  function toggleItemSelection(competenceId) {
    const newSelected = new Set(selectedItems);
    if (newSelected.has(competenceId)) {
      newSelected.delete(competenceId);
    } else {
      newSelected.add(competenceId);
    }
    setSelectedItems(newSelected);
  }

  function toggleSelectAll() {
    if (!selectionEnabled) return;
    if (selectedItems.size === filteredAndSortedItems.length) {
      setSelectedItems(new Set());
    } else {
      setSelectedItems(new Set(filteredAndSortedItems.map(item => item.competenceId)));
    }
  }

  async function handleBulkApprove() {
    if (selectedItems.size === 0) return;

    const usingAll = selectedItems.size === filteredAndSortedItems.length;
    const targetIds = usingAll
      ? filteredAndSortedItems.map(item => item.competenceId)
      : Array.from(selectedItems);
    const count = targetIds.length;

    if (!window.confirm(`Approve ${count} ${count === 1 ? 'competence' : 'competences'}?`)) {
      return;
    }

    setBulkActionLoading(true);
    try {
      await bulkApprove(targetIds, `Bulk approved via UI (${count} items)`);
      showSuccess(`Successfully approved ${count} ${count === 1 ? 'competence' : 'competences'}`);

      const idSet = new Set(targetIds);
      setItems(prev => prev.filter(i => !idSet.has(i.competenceId)));

      setTotalCounts(prev => ({
        ...prev,
        pending: Math.max(0, prev.pending - count),
        approved: prev.approved + count,
      }));

      clampPageAfterRemoval(count);
      setSelectedItems(new Set());
    } catch (err) {
      showError(err, "Failed to approve competences");
    } finally {
      setBulkActionLoading(false);
    }
  }

  // Bulk action: move approved competences to Imported/Completed
  async function handleBulkImported() {
    if (selectedItems.size === 0) return;

    const usingAll = selectedItems.size === filteredAndSortedItems.length;
    const targetIds = usingAll
      ? filteredAndSortedItems.map(item => item.competenceId)
      : Array.from(selectedItems);
    const count = targetIds.length;
    if (
      !window.confirm(
        `Mark ${count} ${count === 1 ? "competence" : "competences"} as Imported/Completed (Imported to Profiler)?`
      )
    ) {
      return;
    }

    setBulkActionLoading(true);
    try {
      await bulkMarkImported(targetIds);
      showSuccess(
        `Successfully marked ${count} ${count === 1 ? "competence" : "competences"} as Imported/Completed`
      );

      // Remove from current Approved view
      const idSet = new Set(targetIds);
      setItems(prev => prev.filter(i => !idSet.has(i.competenceId)));

      // Update counts
      setTotalCounts(prev => ({
        ...prev,
        approved: Math.max(0, prev.approved - count),
        imported: (prev.imported ?? 0) + count,
        archive: (prev.archive ?? 0) + count,
      }));

      clampPageAfterRemoval(count);
      setSelectedItems(new Set());
    } catch (err) {
      showError(err, "Failed to mark competences as Imported/Completed");
    } finally {
      setBulkActionLoading(false);
    }
  }

  // Bulk action: move approved competences back to Pending
  async function handleBulkMoveToPending() {
    if (selectedItems.size === 0) return;

    const usingAll = selectedItems.size === filteredAndSortedItems.length;
    const targetIds = usingAll
      ? filteredAndSortedItems.map(item => item.competenceId)
      : Array.from(selectedItems);
    const count = targetIds.length;

    if (
      !window.confirm(
        `Move ${count} ${count === 1 ? "competence" : "competences"} back to Pending?`
      )
    ) {
      return;
    }

    setBulkActionLoading(true);
    try {
      await bulkMoveToPending(targetIds);
      showSuccess(
        `Successfully moved ${count} ${count === 1 ? "competence" : "competences"} back to Pending`
      );

      const idSet = new Set(targetIds);
      setItems(prev => prev.filter(i => !idSet.has(i.competenceId)));

      setTotalCounts(prev => ({
        ...prev,
        approved: Math.max(0, prev.approved - count),
        pending: (prev.pending ?? 0) + count,
      }));

      clampPageAfterRemoval(count);
      setSelectedItems(new Set());
    } catch (err) {
      showError(err, "Failed to move competences back to Pending");
    } finally {
      setBulkActionLoading(false);
    }
  }

  // Bulk action: delete competences from Approved
  async function handleBulkDelete() {
    if (selectedItems.size === 0) return;

    const usingAll = selectedItems.size === filteredAndSortedItems.length;
    const targetIds = usingAll
      ? filteredAndSortedItems.map(item => item.competenceId)
      : Array.from(selectedItems);
    const count = targetIds.length;

    if (
      !window.confirm(
        `Are you sure you want to permanently delete ${count} ${count === 1 ? "competence" : "competences"}? This cannot be undone.`
      )
    ) {
      return;
    }

    if (
      !window.confirm(
        "Please confirm again: this will permanently delete the selected competences."
      )
    ) {
      return;
    }

    setBulkActionLoading(true);
    try {
      await bulkDelete(targetIds);
      showSuccess(
        `Successfully deleted ${count} ${count === 1 ? "competence" : "competences"}`
      );

      const idSet = new Set(targetIds);
      setItems(prev => prev.filter(i => !idSet.has(i.competenceId)));

      setTotalCounts(prev => ({
        ...prev,
        approved: Math.max(0, prev.approved - count),
      }));

      clampPageAfterRemoval(count);
      setSelectedItems(new Set());
    } catch (err) {
      showError(err, "Failed to delete competences");
    } finally {
      setBulkActionLoading(false);
    }
  }

  function openBulkRejectModal() {
    if (selectedItems.size === 0) return;
    setRejectNotes("");
    setRejectModalItem({ competenceId: null, name: `${selectedItems.size} competences` });
  }

  async function confirmBulkReject() {
    if (selectedItems.size === 0 || !rejectNotes.trim()) return;

    setRejectSubmitting(true);
    try {
      const targetIds = Array.from(selectedItems);
      const count = targetIds.length;

      await bulkReject(targetIds, rejectNotes.trim());
      showSuccess(`Successfully rejected ${count} ${count === 1 ? 'competence' : 'competences'}`);
      // Update local state instead of reloading all data
      const idSet = new Set(targetIds);
      setItems(prev => prev.filter(i => !idSet.has(i.competenceId)));
      setTotalCounts(prev => ({
        ...prev,
        pending: Math.max(0, prev.pending - count),
        rejected: prev.rejected + count
      }));
      clampPageAfterRemoval(count);
      setSelectedItems(new Set());
      closeRejectModal();
    } catch (err) {
      showError(err, "Failed to reject some competences");
    } finally {
      setRejectSubmitting(false);
    }
  }

  async function openCategorizationModal(item) {
    setCategorizationLoading(true);
    try {
      // Load metadata if not already loaded
      if (!metadata) {
        const meta = await getMetadata();
        setMetadata(meta);
      }
      
      // Get full competence details to get IDs
      const detail = await getCompetence(item.competenceId);
      setCategorizationModalItem(detail);
      setEditedCompetenceName(detail.name ?? "");
      setSelectedAreaId(detail.areaId || null);
      setSelectedCategoryId(detail.categoryId || null);
      setSelectedSubcategoryId(detail.subcategoryId || null);
    } catch (err) {
      showError(err, "Failed to load competence details");
    } finally {
      setCategorizationLoading(false);
    }
  }

  function closeCategorizationModal() {
    if (categorizationSubmitting) return;
    setCategorizationModalItem(null);
    setEditedCompetenceName("");
    setSelectedAreaId(null);
    setSelectedCategoryId(null);
    setSelectedSubcategoryId(null);
  }

  async function confirmCategorizationUpdate() {
    if (!categorizationModalItem || !selectedAreaId || !metadata) return;

    setCategorizationSubmitting(true);
    try {
      await updateCategorization(
        categorizationModalItem.competenceId,
        selectedAreaId,
        selectedCategoryId,
        selectedSubcategoryId,
        editedCompetenceName
      );
      
      // Fetch the updated competence from the server to ensure we have the latest data (including name/normalized if changed)
      const updatedCompetence = await getCompetence(categorizationModalItem.competenceId);
      
      // Update the item in state with the fetched data
      setItems(prev => prev.map(item => {
        if (item.competenceId === categorizationModalItem.competenceId) {
          return {
            ...item,
            name: updatedCompetence.name ?? item.name,
            normalized: updatedCompetence.normalized ?? item.normalized,
            areaId: updatedCompetence.areaId || null,
            areaName: updatedCompetence.areaName || null,
            categoryId: updatedCompetence.categoryId || null,
            categoryName: updatedCompetence.categoryName || null,
            subcategoryId: updatedCompetence.subcategoryId || null,
            subcategoryName: updatedCompetence.subcategoryName || null,
          };
        }
        return item;
      }));
      
      showSuccess("Categorization updated successfully");
      closeCategorizationModal();
    } catch (err) {
      const apiMessage = parseErrorMessage(getErrorMessage(err));
      showError(err, apiMessage || "Failed to update categorization");
    } finally {
      setCategorizationSubmitting(false);
    }
  }

  // Load metadata on mount
  useEffect(() => {
    getMetadata().then(setMetadata).catch(err => {
      console.error("Failed to load metadata:", err);
    });
  }, []);

  async function handleDownloadExcel() {
    setExportLoading(true);
    try {
      const allCompetences = await getAllApproved();

      // Extra safety: exclude any legacy/imported competences that were seeded
      // from the legacy ImportCompetences table.
      const exportableCompetences = allCompetences.filter(
        (item) => item.matchedType !== "Seeded"
      );

      const excelData = exportableCompetences.map(item => ({
        Name: item.name || "",
        Area: item.areaName || "",
        Category: item.categoryName || "",
        Subcategory: item.subcategoryName || "",
      }));

      const ws = XLSX.utils.json_to_sheet(excelData);
      const wb = XLSX.utils.book_new();
      XLSX.utils.book_append_sheet(wb, ws, "Approved Competences");

      const today = new Date();
      const dateStr = today.toISOString().split('T')[0];
      const filename = `approved-competences-${dateStr}.xlsx`;

      XLSX.writeFile(wb, filename);
      showSuccess("Excel file downloaded successfully");
    } catch (err) {
      showError(err, "Failed to export competences to Excel");
    } finally {
      setExportLoading(false);
    }
  }

  function SortableHeader({ field, children }) {
    const isActive = sortField === field;
    return (
      <th 
        className={`sortable ${isActive ? "active" : ""}`}
        onClick={() => handleSort(field)}
      >
        {children}
        {isActive && (
          <span className="sort-indicator">
            {sortDirection === "asc" ? "↑" : "↓"}
          </span>
        )}
      </th>
    );
  }

  async function handleRemap() {
    if (selectedItems.size !== 1) {
      showError(null, "Please select exactly one competence to remap.");
      return;
    }

    setLoading(true);
    try {
      const selectedCompetences = items.filter(item => selectedItems.has(item.competenceId));
      const names = selectedCompetences.map(item => item.name);

      // Delete the selected competences
      const deletePromises = selectedCompetences.map(item => deleteCompetence(item.competenceId));
      await Promise.all(deletePromises);

      // Remove from local state
      setItems(prev => prev.filter(item => !selectedItems.has(item.competenceId)));

      // Remap intentionally reruns the full backend pipeline instead of carrying
      // forward the previous confidence; the new pending row reflects the latest
      // LLM match/validation result.
      const mapPromises = names.map(name => mapSingle(name));
      await Promise.all(mapPromises);

      showSuccess(`Remapped competence successfully.`);

      // Clear selection and reload data
      setSelectedItems(new Set());
      await load();

      // Update counts
      const counts = await getCounts();
      setTotalCounts({
        pending: counts.pending ?? 0,
        approved: counts.approved ?? 0,
        rejected: counts.rejected ?? 0,
        legacy: counts.legacy ?? 0,
        imported: counts.imported ?? 0,
        archive: counts.archive ?? 0,
      });
    } catch (err) {
      console.error("Failed to remap:", err);
      showError(err, "Failed to remap competence");
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="page review-page">
      <h1>Review Competences</h1>

      <div className="review-dashboard">
        <div className="card review-kpi-card">
          <div className="review-kpi-label">Total Pending</div>
          <div className="review-kpi-value">{totalPendingCount}</div>
          {status === "pending" && filteredAndSortedItems.length !== totalPendingCount && (
            <div className="review-kpi-subtext muted">
              {filteredAndSortedItems.length} in current view
            </div>
          )}
        </div>
        <div className="card review-kpi-card">
          <div className="review-kpi-label">Total Approved</div>
          <div className="review-kpi-value">{totalApprovedCount}</div>
          {status === "approved" && filteredAndSortedItems.length !== totalApprovedCount && (
            <div className="review-kpi-subtext muted">
              {filteredAndSortedItems.length} in current view
            </div>
          )}
        </div>
        <div className="card review-kpi-card">
          <div className="review-kpi-label">Total Rejected</div>
          <div className="review-kpi-value">{totalRejectedCount}</div>
          {status === "rejected" && filteredAndSortedItems.length !== totalRejectedCount && (
            <div className="review-kpi-subtext muted">
              {filteredAndSortedItems.length} in current view
            </div>
          )}
        </div>
        <div className="card review-kpi-card">
          <div className="review-kpi-label">Total Archived (Legacy + Imported)</div>
          <div className="review-kpi-value">{totalArchiveCount}</div>
          {status === "archive" && filteredAndSortedItems.length !== totalArchiveCount && (
            <div className="review-kpi-subtext muted">
              {filteredAndSortedItems.length} in current view
            </div>
          )}
        </div>
        <div className="card review-kpi-card">
          <div className="review-kpi-label">Reviewed Today</div>
          <div className="review-kpi-value">{itemsReviewedToday}</div>
        </div>
        <div className="card review-kpi-card">
          <div className="review-kpi-label">Avg. Confidence</div>
          <div className="review-kpi-value">
            {averageConfidence == null ? "—" : `${Math.round(averageConfidence * 100)}%`}
          </div>
          {status === "pending" && (
            <div className="review-kpi-subtext muted">
              {lowConfidenceCount} low confidence
            </div>
          )}
        </div>
        {topAreas.length > 0 && (
          <div className="card review-kpi-card review-kpi-card-wide">
            <div className="review-kpi-label">Top Areas</div>
            <div className="review-kpi-list">
              {topAreas.map((area, idx) => (
                <div key={area.name} className="review-kpi-list-item">
                  <span className="review-kpi-list-rank">{idx + 1}.</span>
                  <span className="review-kpi-list-name">{area.name}</span>
                  <span className="review-kpi-list-count muted">{area.count}</span>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>

      <div className="review-toolbar">
        <div className="review-tabs">
          {TABS.map(t => (
            <button
              key={t}
              onClick={() => setStatus(t)}
              className={`btn btn-tab-${t} ${status === t ? 'active' : ''} review-tab-button`}
            >
              {t.toUpperCase()}
            </button>
          ))}
        </div>

        <div className="review-search">
          <input
            type="search"
            className="input review-search-input"
            placeholder="Search by name or normalized…"
            value={searchQuery}
            onChange={e => setSearchQuery(e.target.value)}
          />
        </div>

        <div className="review-filters muted">
          <label>
            Area
            <select
              value={filters.area}
              onChange={e => setFilters(prev => ({ ...prev, area: e.target.value }))}
            >
              <option value="">All</option>
              {uniqueFilterValues.areas.map(a => (
                <option key={a} value={a}>{a}</option>
              ))}
            </select>
          </label>
          <label>
            Category
            <select
              value={filters.category}
              onChange={e => setFilters(prev => ({ ...prev, category: e.target.value }))}
            >
              <option value="">All</option>
              {uniqueFilterValues.categories.map(c => (
                <option key={c} value={c}>{c}</option>
              ))}
            </select>
          </label>
          <label>
            Subcategory
            <select
              value={filters.subcategory}
              onChange={e => setFilters(prev => ({ ...prev, subcategory: e.target.value }))}
            >
              <option value="">All</option>
              {uniqueFilterValues.subcategories.map(s => (
                <option key={s} value={s}>{s}</option>
              ))}
            </select>
          </label>
          <label>
            Matched
            <select
              value={filters.matchedType}
              onChange={e => setFilters(prev => ({ ...prev, matchedType: e.target.value }))}
            >
              <option value="">All</option>
              {uniqueFilterValues.matchedTypes.map(m => (
                <option key={m} value={m}>{m}</option>
              ))}
            </select>
          </label>
        </div>

        <div className="review-sort-controls">
          <label className="muted review-sort-label">
            Sort by:
            <select
              value={sortField}
              onChange={(e) => {
                setSortField(e.target.value);
                setSortDirection("desc");
                setPage(0);
              }}
              className="review-sort-select"
            >
              <option value="createdAt">Created Date</option>
              <option value="reviewedAt">Review Date</option>
              <option value="name">Name</option>
              <option value="confidence">Confidence</option>
              <option value="area">Area</option>
            </select>
          </label>
          <button
            className="btn btn-sm btn-ghost"
            onClick={() => {
              setSortDirection(sortDirection === "asc" ? "desc" : "asc");
            }}
            title={`Sort ${sortDirection === "asc" ? "descending" : "ascending"}`}
            aria-label={`Sort ${sortDirection === "asc" ? "descending" : "ascending"}`}
          >
            {sortDirection === "asc" ? "↑" : "↓"}
          </button>
        </div>

        <div className="review-page-size">
          <label className="muted review-page-size-label">
            Page size:
            <select
              value={pageSize}
              onChange={(e) => {
                setPageSize(Number(e.target.value));
                setPage(0);
              }}
              className="review-page-size-select"
            >
              <option value={5}>5</option>
              <option value={10}>10</option>
              <option value={25}>25</option>
              <option value={50}>50</option>
            </select>
          </label>
        </div>

        <button
          className="btn btn-ghost"
          onClick={handleRemap}
          disabled={loading || selectedItems.size !== 1}
          title="Remap selected competence using AI (select exactly one)"
        >
          {loading ? "Remapping..." : "Reload AI Mapping"}
        </button>

        {status === "approved" && (
          <button
            className="btn btn-approve"
            onClick={handleDownloadExcel}
            disabled={exportLoading}
          >
            {exportLoading ? "Exporting..." : "Download Excel"}
          </button>
        )}

        {status === "pending" && selectedItems.size > 0 && (
          <div className="review-bulk-actions">
            <span className="muted review-bulk-count">
              {selectedItems.size} {selectedItems.size === 1 ? 'item' : 'items'} selected
            </span>
            <button
              className="btn btn-approve btn-sm"
              onClick={handleBulkApprove}
              disabled={bulkActionLoading}
            >
              {bulkActionLoading ? "Approving..." : `Approve ${selectedItems.size}`}
            </button>
            <button
              className="btn btn-reject btn-sm"
              onClick={openBulkRejectModal}
              disabled={bulkActionLoading}
            >
              Reject {selectedItems.size}
            </button>
            <button
              className="btn btn-ghost btn-sm"
              onClick={() => setSelectedItems(new Set())}
              disabled={bulkActionLoading}
            >
              Clear
            </button>
          </div>
        )}

        {status === "approved" && selectedItems.size > 0 && (
          <div className="review-bulk-actions">
            <span className="muted review-bulk-count">
              {selectedItems.size} {selectedItems.size === 1 ? "item" : "items"} selected
            </span>
            <button
              className="btn btn-approve btn-sm"
              onClick={handleBulkImported}
              disabled={bulkActionLoading}
            >
              {bulkActionLoading ? "Marking..." : "Imported to Profiler"}
            </button>
            <button
              className="btn btn-sm"
              onClick={handleBulkMoveToPending}
              disabled={bulkActionLoading}
            >
              Move to Pending
            </button>
            <button
              className="btn btn-reject btn-sm"
              onClick={handleBulkDelete}
              disabled={bulkActionLoading}
            >
              Delete
            </button>
            <button
              className="btn btn-ghost btn-sm"
              onClick={() => setSelectedItems(new Set())}
              disabled={bulkActionLoading}
            >
              Clear
            </button>
          </div>
        )}
      </div>

      {error && <p className="review-error-text">{error}</p>}

      {loading && (
        status === 'pending' ? (
          <div className="review-cards">
            {Array.from({ length: 3 }).map((_, i) => (
              <SkeletonCard key={i} />
            ))}
          </div>
        ) : (
          <SkeletonTable
            rows={5}
            columns={
              (status === "approved" ? 1 : 0) + // selection column for approved
              10 + // base columns
              (status === "approved" || status === "rejected" ? 1 : 0) // reviewed column
            }
          />
        )
      )}

      {!loading && !error && (
        status === 'pending' ? (
          <div className="review-cards">
            {paginatedItems.map(item => {
              const notes = item.reviewNotes ?? "";

              const isSelected = selectedItems.has(item.competenceId);

              return (
                <div 
                  className={`review-card ${isSelected ? 'review-card-selected' : ''} review-card-clickable`} 
                  key={item.competenceId}
                  onClick={(e) => {
                    // Don't toggle if clicking on buttons or links
                    if (e.target.closest('button') || e.target.closest('a')) return;
                    toggleItemSelection(item.competenceId);
                  }}
                  role="button"
                  tabIndex={0}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                      e.preventDefault();
                      toggleItemSelection(item.competenceId);
                    }
                  }}
                  aria-label={`Select ${item.name}`}
                >
                  <div className="card-left card">
                    <div>
                      <div className="competence-name">{item.name}</div>
                      <div className="muted review-card-meta">
                        <div><strong>Normalized:</strong> {item.normalized || "—"}</div>
                        <div><strong>Area:</strong> {item.areaName ?? '—'}</div>
                        <div><strong>Category:</strong> {item.categoryName ?? '—'}</div>
                        <div><strong>Subcategory:</strong> {item.subcategoryName ?? '—'}</div>
                        <div><strong>Matched Type:</strong> {item.matchedType ?? '—'}</div>
                        <div className="review-card-meta-row">
                          <strong>Confidence:</strong> <ConfidenceBadge confidence={item.confidence} />
                        </div>
                        <div className="review-card-meta-row">
                          <strong>Created:</strong> <span title={formatDateFull(item.createdAt)}>{formatDate(item.createdAt)}</span>
                        </div>
                      </div>
                    </div>
                    <div className="actions review-card-actions">
                      <button className="btn btn-sm" onClick={() => openCategorizationModal(item)}>Edit Categorization</button>
                      <button className="btn btn-approve btn-sm" onClick={() => handleApprove(item)}>Approve</button>
                      <button className="btn btn-reject btn-sm" onClick={() => openRejectModal(item)}>Reject</button>
                      <button className="btn btn-other btn-sm" onClick={() => handleAssignOther(item)}>Other</button>
                      <button className="btn btn-reject btn-sm" onClick={() => handleDelete(item)}>Delete</button>
                    </div>
                  </div>
                  <div className="card-right card review-card-notes">
                    <div className="muted review-card-notes-text">
                      <strong>Review Notes:</strong>
                      <div
                        className={`col-notes review-card-notes-body ${notes ? "is-clickable" : ""}`}
                        onClick={() => notes && setSelectedNote(notes)}
                        title={notes ? "Click to view full notes" : ""}
                      >
                        {notes || <span className="muted">—</span>}
                      </div>
                    </div>
                  </div>
                </div>
              );
            })}

            {paginatedItems.length === 0 && filteredAndSortedItems.length === 0 && (
              <div className="card">No competences found.</div>
            )}
            {paginatedItems.length === 0 && filteredAndSortedItems.length > 0 && (
              <div className="card">No competences found on this page. Try adjusting filters or going to a different page.</div>
            )}
          </div>
        ) : (
          <div className="table-container">
            <table className="review-table">
              <thead>
                <tr>
                  {selectionEnabled && (
                    <th className="review-table-checkbox-header">
                      <input
                        type="checkbox"
                        checked={filteredAndSortedItems.length > 0 && selectedItems.size === filteredAndSortedItems.length}
                        onChange={toggleSelectAll}
                        className="review-table-checkbox"
                        aria-label="Select all items"
                        title="Select all"
                      />
                    </th>
                  )}
                  <SortableHeader field="name">Name</SortableHeader>
                  <th>Normalized</th>
                  <SortableHeader field="area">Area</SortableHeader>
                  <th>Category</th>
                  <th>Subcategory</th>
                  <th>Matched Type</th>
                  <SortableHeader field="confidence">Confidence</SortableHeader>
                  <SortableHeader field="createdAt">Created</SortableHeader>
                  {status === "approved" && <th>Reviewed</th>}
                  {status === "rejected" && <th>Reviewed</th>}
                  <th className="col-notes">Review Notes</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                {paginatedItems.map(item => {
                  const notes = item.reviewNotes ?? "";
                  const isExpanded = expandedRows.has(item.competenceId);
                  const isSelected = selectionEnabled && selectedItems.has(item.competenceId);
                  const short = notes.length > 100 ? notes.slice(0, 100) + '…' : notes;
                  
                  return (
                    <tr 
                      key={item.competenceId} 
                      className={`${isExpanded ? "expanded" : ""} ${isSelected ? "review-table-row-selected" : ""} review-table-row-clickable`}
                      onClick={(e) => {
                        // Don't toggle if clicking on buttons, links, or expandable notes
                        if (e.target.closest('button') || e.target.closest('a') || e.target.closest('.col-notes.is-clickable')) return;
                        if (!selectionEnabled) return;
                        toggleItemSelection(item.competenceId);
                      }}
                      role="button"
                      tabIndex={0}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.preventDefault();
                          if (!selectionEnabled) return;
                          toggleItemSelection(item.competenceId);
                        }
                      }}
                      aria-label={`Select ${item.name}`}
                    >
                      {selectionEnabled && (
                        <td className="review-table-checkbox-cell">
                          {isSelected && <span className="review-table-selected-indicator">✓</span>}
                        </td>
                      )}
                      <td className="competence-name">{item.name}</td>
                      <td className="muted review-table-cell-small">{item.normalized || "—"}</td>
                      <td>{item.areaName || "—"}</td>
                      <td>{item.categoryName || "—"}</td>
                      <td>{item.subcategoryName || "—"}</td>
                      <td className="muted review-table-cell-small">{item.matchedType || "—"}</td>
                      <td><ConfidenceBadge confidence={item.confidence} /></td>
                      <td className="muted review-table-cell-small" title={formatDateFull(item.createdAt)}>
                        {formatDate(item.createdAt)}
                      </td>
                      {(status === "approved" || status === "rejected") && (
                        <td className="muted review-table-cell-small">
                          {item.reviewedAt ? (
                            <span title={formatDateFull(item.reviewedAt)}>{formatDate(item.reviewedAt)}</span>
                          ) : "—"}
                        </td>
                      )}
                      <td 
                        className={`muted col-notes review-table-notes ${isExpanded ? "expanded" : ""} ${notes ? "is-clickable" : ""}`}
                        onClick={() => notes && toggleRow(item.competenceId)}
                        title={notes ? (isExpanded ? "Click to collapse" : "Click to expand") : ""}
                      >
                        {notes ? (isExpanded ? notes : short) : <span className="muted">—</span>}
                        {notes && notes.length > 100 && (
                          <span className="expand-hint">
                            {isExpanded ? " [collapse]" : " [expand]"}
                          </span>
                        )}
                      </td>
                      <td className="review-table-actions">
                        <button
                          className="btn btn-reject btn-sm"
                          onClick={(e) => {
                            e.stopPropagation();
                            handleDelete(item);
                          }}
                        >
                          Delete
                        </button>
                      </td>
                    </tr>
                  );
                })}
                {paginatedItems.length === 0 && filteredAndSortedItems.length === 0 && (
                  <tr>
                    <td colSpan={tableColumnCount}>No competences found.</td>
                  </tr>
                )}
                {paginatedItems.length === 0 && filteredAndSortedItems.length > 0 && (
                  <tr>
                    <td colSpan={tableColumnCount}>No competences found on this page. Try adjusting filters or going to a different page.</td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )
      )}

      <div className="review-pagination">
        <button 
          className="btn" 
          onClick={() => setPage(p => Math.max(0, p - 1))} 
          disabled={page === 0 || loading}
        >
          Previous
        </button>
        <span className="muted review-pagination-page">Page {page + 1}</span>
        <button 
          className="btn" 
          onClick={() => { if (hasMore) setPage(p => p + 1); }} 
          disabled={!hasMore || loading}
        >
          Next
        </button>
        <span className="muted review-pagination-summary">
          Showing {paginatedItems.length} of {filteredAndSortedItems.length} {filteredAndSortedItems.length === 1 ? 'item' : 'items'}
        </span>
      </div>

      {selectedNote && (
        <div className="note-modal-overlay" onClick={() => setSelectedNote(null)}>
          <div className="note-modal-content" onClick={e => e.stopPropagation()}>
            <div className="note-modal-header">
              <strong>Review Notes</strong>
              <button className="btn" onClick={() => setSelectedNote(null)}>Close</button>
            </div>
            <div className="note-modal-body">{selectedNote}</div>
          </div>
        </div>
      )}

      {rejectModalItem && (
        <div className="note-modal-overlay" onClick={closeRejectModal} aria-modal="true" role="dialog">
          <div className="note-modal-content" onClick={e => e.stopPropagation()}>
            <div className="note-modal-header">
              <strong>Reject competence</strong>
              <button
                type="button"
                className="btn btn-sm btn-ghost"
                onClick={closeRejectModal}
                disabled={rejectSubmitting}
                aria-label="Close reject dialog"
              >
                Close
              </button>
            </div>
            <div className="note-modal-body">
              <p className="muted">
                Please provide a short explanation for rejecting{" "}
                <strong>
                  {rejectModalItem.competenceId === null 
                    ? `${selectedItems.size} competences` 
                    : rejectModalItem.name}
                </strong>{" "}
                so others understand the decision.
              </p>
              <textarea
                className="input"
                rows={4}
                value={rejectNotes}
                onChange={e => setRejectNotes(e.target.value)}
                placeholder="Example: Overlaps with existing competence in Area X / Not relevant for this program / Too generic…"
              />
              <div className="actions" style={{ marginTop: "0.75rem" }}>
                <button
                  type="button"
                  className="btn btn-reject"
                  onClick={confirmReject}
                  disabled={rejectSubmitting || !rejectNotes.trim()}
                >
                  {rejectSubmitting ? "Rejecting…" : "Confirm reject"}
                </button>
                <button
                  type="button"
                  className="btn btn-ghost btn-sm"
                  onClick={closeRejectModal}
                  disabled={rejectSubmitting}
                >
                  Cancel
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {loading && (
        <div className="review-loading-overlay" aria-hidden="true">
          <div className="review-loading-spinner" />
          <div className="review-loading-text muted">Loading competences…</div>
        </div>
      )}

      {categorizationModalItem && (
        <div className="note-modal-overlay" onClick={closeCategorizationModal} aria-modal="true" role="dialog">
          <div className="note-modal-content" onClick={e => e.stopPropagation()}>
            <div className="note-modal-header">
              <strong>Edit Categorization</strong>
              <button
                type="button"
                className="btn btn-sm btn-ghost"
                onClick={closeCategorizationModal}
                disabled={categorizationSubmitting}
                aria-label="Close categorization editor"
              >
                Close
              </button>
            </div>
            <div className="note-modal-body">
              {categorizationLoading ? (
                <div className="muted">Loading...</div>
              ) : metadata ? (
                <>
                  <p className="muted" style={{ marginBottom: "1rem" }}>
                    Update the categorization and optionally the competence name. The competence will remain in pending status.
                    If you change the name, it is checked for duplicates (including legacy).
                  </p>
                  
                  <div style={{ display: "flex", flexDirection: "column", gap: "1rem" }}>
                    <label>
                      <strong>Kompetensnamn</strong>
                      <input
                        type="text"
                        className="input"
                        value={editedCompetenceName}
                        onChange={(e) => setEditedCompetenceName(e.target.value)}
                        placeholder="Competence name"
                        disabled={categorizationSubmitting}
                      />
                    </label>
                    <label>
                      <strong>Area *</strong>
                      <select
                        className="input"
                        value={selectedAreaId || ""}
                        onChange={(e) => {
                          const newAreaId = e.target.value ? e.target.value : null;
                          setSelectedAreaId(newAreaId);
                          // Reset category and subcategory when area changes
                          setSelectedCategoryId(null);
                          setSelectedSubcategoryId(null);
                        }}
                        disabled={categorizationSubmitting}
                      >
                        <option value="">Select an area...</option>
                        {metadata.areas.map(area => (
                          <option key={area.areaId} value={area.areaId}>{area.name}</option>
                        ))}
                      </select>
                    </label>

                    <label>
                      <strong>Category</strong>
                      <select
                        className="input"
                        value={selectedCategoryId || ""}
                        onChange={(e) => {
                          const newCategoryId = e.target.value ? e.target.value : null;
                          setSelectedCategoryId(newCategoryId);
                          // Reset subcategory when category changes
                          setSelectedSubcategoryId(null);
                        }}
                        disabled={categorizationSubmitting || !selectedAreaId}
                      >
                        <option value="">None</option>
                        {metadata.categories
                          .filter(cat => cat.areaId === selectedAreaId)
                          .map(category => (
                            <option key={category.categoryId} value={category.categoryId}>{category.name}</option>
                          ))}
                      </select>
                    </label>

                    <label>
                      <strong>Subcategory</strong>
                      <select
                        className="input"
                        value={selectedSubcategoryId || ""}
                        onChange={(e) => {
                          setSelectedSubcategoryId(e.target.value ? e.target.value : null);
                        }}
                        disabled={categorizationSubmitting || !selectedCategoryId}
                      >
                        <option value="">None</option>
                        {metadata.subcategories
                          .filter(sub => sub.categoryId === selectedCategoryId)
                          .map(subcategory => (
                            <option key={subcategory.subcategoryId} value={subcategory.subcategoryId}>{subcategory.name}</option>
                          ))}
                      </select>
                    </label>
                  </div>

                  <div className="actions" style={{ marginTop: "1rem" }}>
                    <button
                      type="button"
                      className="btn btn-approve"
                      onClick={confirmCategorizationUpdate}
                      disabled={categorizationSubmitting || !selectedAreaId}
                    >
                      {categorizationSubmitting ? "Saving…" : "Save Changes"}
                    </button>
                    <button
                      type="button"
                      className="btn btn-ghost btn-sm"
                      onClick={closeCategorizationModal}
                      disabled={categorizationSubmitting}
                    >
                      Cancel
                    </button>
                  </div>
                </>
              ) : (
                <div className="muted">Loading metadata...</div>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}