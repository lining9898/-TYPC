;;; ============================================================
;;; TYPC Toolbox - AutoCAD 2016 loader
;;; DLL  : TYPC.dll (v1.9.1, acdbmgd/acmgd 20.1 = AutoCAD 2016)
;;; Note : self-contained - no extra DLL has to sit next to it.
;;;
;;; HOW TO USE
;;;   1) Manual : APPLOAD -> pick this file
;;;               (or drag this file into the drawing area)
;;;   2) Auto   : APPLOAD -> Startup Suite -> Contents... -> add
;;;               this file. It then loads on every drawing.
;;;
;;; If you move TYPC.dll, edit the path on the line below.
;;; ============================================================

(vl-load-com)

;;; --- EDIT THIS LINE IF YOU MOVE TYPC.DLL ---------------------
(setq *TYPC-DLL-PATH* "D:\\ai\\TYPC\\dist\\AutoCAD2016\\TYPC.dll")
;;; ------------------------------------------------------------

(defun TYPC-LOAD (/ dll result)
  (if (vl-bb-ref 'TYPC:LOADED)
    (princ)
    (progn
      (setq dll
        (cond
          ((findfile *TYPC-DLL-PATH*) *TYPC-DLL-PATH*)
          ((findfile "TYPC.dll") (findfile "TYPC.dll"))
          (T nil)))
      (if (null dll)
        (progn
          (princ "\n[TYPC] ERROR - TYPC.dll not found.")
          (princ (strcat "\n[TYPC] Looked for: " *TYPC-DLL-PATH*))
          (princ "\n[TYPC] Fix: edit *TYPC-DLL-PATH* in this .lsp")
          (princ "\n[TYPC]      or run NETLOAD and pick TYPC.dll."))
        (progn
          (setq result
            (vl-catch-all-apply 'command (list "_NETLOAD" dll)))
          (if (vl-catch-all-error-p result)
            (progn
              (princ "\n[TYPC] ERROR - NETLOAD was cancelled/failed.")
              (princ "\n[TYPC] If Windows blocked the file:")
              (princ "\n[TYPC] right-click TYPC.dll -> Properties")
              (princ "\n[TYPC] -> Unblock, or run NETLOAD manually."))
            (progn
              (vl-bb-set 'TYPC:LOADED T)
              (princ (strcat "\n[TYPC] loaded: " dll))
              (princ "\n[TYPC] TYPC / TYY / THUI  - toolbox panel")
              (princ "\n[TYPC] THBH / THSZ / THCS / THBJ - numbers")
              (princ "\n[TYPC] BBLK / BATCHBLOCK - batch make blocks")
              (princ "\n[TYPC] SELBLK - select same-name blocks")
              (princ "\n[TYPC] ADDBLK - add entities into a block")
              (princ "\n[TYPC] WZXX / TAXX - text & pattern linetype")
              (princ "\n[TYPC] WZQF - text prefix/suffix")
              (princ "\n[TYPC] TYPCV - show version")))))))
  (princ))

;;; Run now (covers APPLOAD / drag-and-drop).
(TYPC-LOAD)

;;; Also hook S::STARTUP so the Startup Suite path works even when
;;; COMMAND is not yet usable at load time.
(defun-q TYPC:STARTUP ()
  (TYPC-LOAD)
  (princ))

(if (and (boundp 'S::STARTUP) (listp S::STARTUP))
  (setq S::STARTUP (append S::STARTUP TYPC:STARTUP))
  (setq S::STARTUP TYPC:STARTUP))

(princ)
