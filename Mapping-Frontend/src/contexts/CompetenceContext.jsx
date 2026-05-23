import { createContext, useContext, useState, useCallback } from "react";
import {
  getPending,
  getApproved,
  getRejected,
  getAllApproved,
  approveCompetence,
  rejectCompetence,
  assignToOther,
} from "../api/reviewApi";
import { showError, showSuccess } from "../utils/errorHandler";

const CompetenceContext = createContext(null);

export function CompetenceProvider({ children }) {
  const [pendingCompetences, setPendingCompetences] = useState([]);
  const [approvedCompetences, setApprovedCompetences] = useState([]);
  const [rejectedCompetences, setRejectedCompetences] = useState([]);
  const [loading, setLoading] = useState({
    pending: false,
    approved: false,
    rejected: false,
  });
  const [errors, setErrors] = useState({
    pending: null,
    approved: null,
    rejected: null,
  });

  const loadPending = useCallback(async (skip = 0, take = 100) => {
    setLoading((prev) => ({ ...prev, pending: true }));
    setErrors((prev) => ({ ...prev, pending: null }));
    try {
      const data = await getPending(skip, take);
      setPendingCompetences(data || []);
      return data || [];
    } catch (err) {
      const errorMsg = "Failed to load pending competences";
      setErrors((prev) => ({ ...prev, pending: errorMsg }));
      showError(err, errorMsg);
      throw err;
    } finally {
      setLoading((prev) => ({ ...prev, pending: false }));
    }
  }, []);

  const loadApproved = useCallback(async (skip = 0, take = 50) => {
    setLoading((prev) => ({ ...prev, approved: true }));
    setErrors((prev) => ({ ...prev, approved: null }));
    try {
      const data = await getApproved(skip, take);
      setApprovedCompetences(data || []);
      return data || [];
    } catch (err) {
      const errorMsg = "Failed to load approved competences";
      setErrors((prev) => ({ ...prev, approved: errorMsg }));
      showError(err, errorMsg);
      throw err;
    } finally {
      setLoading((prev) => ({ ...prev, approved: false }));
    }
  }, []);

  const loadRejected = useCallback(async (skip = 0, take = 50) => {
    setLoading((prev) => ({ ...prev, rejected: true }));
    setErrors((prev) => ({ ...prev, rejected: null }));
    try {
      const data = await getRejected(skip, take);
      setRejectedCompetences(data || []);
      return data || [];
    } catch (err) {
      const errorMsg = "Failed to load rejected competences";
      setErrors((prev) => ({ ...prev, rejected: errorMsg }));
      showError(err, errorMsg);
      throw err;
    } finally {
      setLoading((prev) => ({ ...prev, rejected: false }));
    }
  }, []);

  const handleApprove = useCallback(async (competenceId, reviewNotes = null) => {
    try {
      await approveCompetence(competenceId, reviewNotes);
      showSuccess("Competence approved successfully");
      // Refresh all lists
      await Promise.all([
        loadPending(),
        loadApproved(),
      ]);
    } catch (err) {
      showError(err, "Failed to approve competence");
      throw err;
    }
  }, [loadPending, loadApproved]);

  const handleReject = useCallback(async (competenceId, reviewNotes) => {
    try {
      await rejectCompetence(competenceId, reviewNotes);
      showSuccess("Competence rejected successfully");
      // Refresh all lists
      await Promise.all([
        loadPending(),
        loadRejected(),
      ]);
    } catch (err) {
      showError(err, "Failed to reject competence");
      throw err;
    }
  }, [loadPending, loadRejected]);

  const handleAssignOther = useCallback(async (competenceId, reviewNotes = null) => {
    try {
      await assignToOther(competenceId, reviewNotes);
      showSuccess("Competence assigned to 'Other' area successfully");
      // Refresh pending list
      await loadPending();
    } catch (err) {
      showError(err, "Failed to assign competence to Other");
      throw err;
    }
  }, [loadPending]);

  const exportAllApproved = useCallback(async () => {
    try {
      const data = await getAllApproved();
      showSuccess("Approved competences loaded for export");
      return data || [];
    } catch (err) {
      showError(err, "Failed to load approved competences for export");
      throw err;
    }
  }, []);

  const value = {
    // State
    pendingCompetences,
    approvedCompetences,
    rejectedCompetences,
    loading,
    errors,
    // Actions
    loadPending,
    loadApproved,
    loadRejected,
    handleApprove,
    handleReject,
    handleAssignOther,
    exportAllApproved,
    // Helpers
    refreshAll: useCallback(async () => {
      await Promise.all([
        loadPending(),
        loadApproved(),
        loadRejected(),
      ]);
    }, [loadPending, loadApproved, loadRejected]),
  };

  return (
    <CompetenceContext.Provider value={value}>
      {children}
    </CompetenceContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function useCompetence() {
  const context = useContext(CompetenceContext);
  if (!context) {
    throw new Error("useCompetence must be used within a CompetenceProvider");
  }
  return context;
}